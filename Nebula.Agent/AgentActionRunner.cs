using System.Text.Json;
using System.Text.RegularExpressions;

using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Agent.Domain;
using Nebula.Agent.Infrastructure;
using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.Agent;

public sealed class AgentActionRunner : IAgentActionRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILlamaClient llamaClient;
    private readonly IShellExecutor executor;
    private readonly IJsonExtractor jsonExtractor;
    private readonly ILogger logger;
    private readonly CommandValidationService commandValidationService;
    private readonly CommandAuditService commandAuditService;
    private readonly int defaultMaxRetriesPerStep;
    private readonly int defaultMaxSteps;

    public AgentActionRunner(
        ILlamaClient llamaClient,
        IShellExecutor executor,
        IJsonExtractor jsonExtractor,
        ILogger logger,
        ICommandRepository? commandRepository = null,
        int maxRetries = AgentActionRunRequest.DefaultMaxRetriesPerStep,
        int maxSteps = AgentActionRunRequest.DefaultMaxSteps)
    {
        this.llamaClient = llamaClient;
        this.executor = executor;
        this.jsonExtractor = jsonExtractor;
        this.logger = logger;
        commandValidationService = new CommandValidationService(llamaClient);
        commandAuditService = new CommandAuditService(commandRepository, logger);
        defaultMaxRetriesPerStep = Math.Max(0, maxRetries);
        defaultMaxSteps = Math.Max(1, maxSteps);
    }

    public async Task<ConversationTurn> RunAsync(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var session = new AgentActionSession(
            request,
            progress,
            logger,
            llamaClient.SelectedModel,
            defaultMaxSteps,
            defaultMaxRetriesPerStep);

        try
        {
            return await RunCoreAsync(session, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return session.Cancel();
        }
    }

    public async Task<AgentActionDecision> GenerateNextStepAsync(
        AgentActionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Objective);

        var decisionPrompt = CreateDecisionPrompt(request);
        var rawResponse = await llamaClient.GetResponseAsync(
            decisionPrompt,
            progress: null,
            cancellationToken);
        var responsePayload = ModelResponse.Parse(rawResponse).Response;
        var json = ExtractJsonObject(responsePayload);
        var decision = JsonSerializer.Deserialize<AgentActionDecision>(json, JsonOptions)
            ?? throw new JsonException("The model returned an empty ReAct decision.");

        ValidateDecision(decision);
        return decision;
    }

    [Obsolete("Use GenerateNextStepAsync for ReAct execution.")]
    public async Task<string> GeneratePlanAsync(
        string userRequest,
        string chatHistoryContext,
        IReadOnlyList<string>? previousFailures = null,
        CancellationToken cancellationToken = default)
    {
        var decision = await GenerateNextStepAsync(new AgentActionDecisionRequest
        {
            Objective = userRequest,
            ChatHistoryContext = chatHistoryContext,
            CurrentPlan = previousFailures is { Count: > 0 }
                ? string.Join(Environment.NewLine, previousFailures)
                : "No actions completed yet.",
            PreviousActionResult = previousFailures?.LastOrDefault(),
            Observations = previousFailures ?? [],
            StepNumber = 1,
            RetryNumber = previousFailures?.Count ?? 0
        }, cancellationToken);

        return JsonSerializer.Serialize(decision, JsonOptions);
    }

    public Task<ActionValidationResult> ValidateAsync(
        AgentActionRunRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ActionRequestValidator.Validate(request));
    }

    private async Task<ConversationTurn> RunCoreAsync(
        AgentActionSession session,
        CancellationToken cancellationToken)
    {
        session.EmitValidationStarted();

        var requestValidation = await ValidateAsync(session.Request, cancellationToken);
        if (!requestValidation.IsValid)
        {
            return session.BlockUnsafeRequest(requestValidation.Reason);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var decisionAttempt = await TryGenerateDecisionAsync(session, cancellationToken);
            if (!decisionAttempt.Succeeded)
            {
                var terminalTurn = ScheduleRetryOrFail(session, decisionAttempt.Failure!);
                if (terminalTurn is not null)
                {
                    return terminalTurn;
                }

                continue;
            }

            var decision = decisionAttempt.Decision!;
            session.EmitReasoning(decision.ReasoningSummary);

            if (decision.IsComplete)
            {
                return session.Complete(decision.CompletionMessage);
            }

            if (session.StepLimitExceeded)
            {
                return session.FailStepLimit();
            }

            var actionResult = await ExecuteActionAsync(
                session,
                decision.Action!,
                cancellationToken);
            if (actionResult.TerminalTurn is not null)
            {
                return actionResult.TerminalTurn;
            }

            if (actionResult.RequiresRetry)
            {
                var terminalTurn = ScheduleRetryOrFail(session, actionResult.Observation);
                if (terminalTurn is not null)
                {
                    return terminalTurn;
                }

                continue;
            }

            session.CompleteStep(decision.Action!.Objective, actionResult.Observation);
        }
    }

    private async Task<DecisionAttempt> TryGenerateDecisionAsync(
        AgentActionSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var decision = await GenerateNextStepAsync(
                session.CreateDecisionRequest(),
                cancellationToken);
            return DecisionAttempt.Success(decision);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            var failure = $"The next-action decision was invalid: {ex.Message}";
            session.RecordDecisionFailure(failure, ex.Message);
            return DecisionAttempt.Failed(failure);
        }
    }

    private async Task<ActionAttemptResult> ExecuteActionAsync(
        AgentActionSession session,
        AgentToolAction action,
        CancellationToken cancellationToken)
    {
        var execution = session.CreateExecution(action);
        var storedCommand = await commandAuditService.SaveCommandAsync(
            session.Request.RequestId,
            execution,
            cancellationToken);
        var validation = await commandValidationService.ValidateAsync(
            execution,
            cancellationToken);

        await commandAuditService.SaveVerificationAsync(
            storedCommand?.Id,
            execution,
            cancellationToken);
        session.Commands.Add(execution);

        if (!validation.Safe)
        {
            return ActionAttemptResult.Terminal(session.BlockUnsafeCommand(execution));
        }

        if (!validation.Correct)
        {
            var observation = execution.Notes
                ?? "The proposed action does not satisfy the current step.";
            session.RecordCommandObservation(execution.Run, observation);
            return ActionAttemptResult.Retry(observation);
        }

        session.EmitActionStarted(action);
        var toolResponse = await ExecuteToolAsync(execution, cancellationToken);
        session.EmitActionCompleted(execution);

        var observationMessage = BuildObservationMessage(execution, toolResponse);
        session.EmitToolObservation(execution, observationMessage, toolResponse);
        session.RecordObservation(execution.Run, observationMessage);

        await commandAuditService.UpdateExecutionAsync(
            storedCommand?.Id,
            execution.Executed,
            execution.Executed ? toolResponse : execution.Notes,
            cancellationToken);

        return execution.Executed
            ? ActionAttemptResult.Completed(observationMessage)
            : ActionAttemptResult.Retry(observationMessage);
    }

    private async Task<string> ExecuteToolAsync(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        try
        {
            var toolResponse = await executor.RunCommandAsync(execution.Run, cancellationToken);
            ApplyToolResponse(execution, toolResponse);
            return toolResponse;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            execution.Error = ex.Message;
            execution.Notes = $"Falha ao executar o comando: {ex.Message}";
            return ex.Message;
        }
    }

    private static void ApplyToolResponse(CommandExecution execution, string toolResponse)
    {
        execution.Output = toolResponse;

        if (ToolResponseIndicatesFailure(toolResponse))
        {
            execution.Error = "Tool response indicated failure.";
            execution.Notes = "Falha detectada ao inspecionar a resposta da ferramenta.";
            return;
        }

        execution.Executed = true;
        execution.Output = toolResponse;
        execution.Notes = string.IsNullOrWhiteSpace(toolResponse)
            ? "Comando executado sem saida textual."
            : "Comando executado com sucesso.";
    }

    private static ConversationTurn? ScheduleRetryOrFail(
        AgentActionSession session,
        string failure)
    {
        return session.TryScheduleRetry(failure)
            ? null
            : session.FailRetryLimit(failure);
    }

    private static string BuildObservationMessage(
        CommandExecution execution,
        string toolResponse)
    {
        if (!execution.Executed)
        {
            return execution.Notes ?? toolResponse;
        }

        return string.IsNullOrWhiteSpace(toolResponse)
            ? "The tool completed successfully without textual output."
            : toolResponse;
    }

    private string ExtractJsonObject(string input)
    {
        try
        {
            return jsonExtractor.ExtractJsonObject(input);
        }
        catch (ArgumentException ex)
        {
            logger.LogError($"Error extracting JSON: {ex.Message}");
            throw;
        }
    }

    private static string CreateDecisionPrompt(AgentActionDecisionRequest request)
    {
        return $$"""
            You are Nebula's ReAct action controller.

            Choose exactly one next action, or declare the objective complete.
            Use only Nebula's existing terminal/file execution tool. File operations must be expressed as shell commands.
            Never reveal chain-of-thought, hidden reasoning, or private analysis.
            The reasoningSummary must be a concise user-visible summary of the next practical need, at most two sentences.
            Use the previous action result and accumulated observations to correct failures.
            When RetryNumber is greater than zero, correct the same logical step instead of silently skipping it.
            Do not claim completion unless the observations demonstrate that the objective is complete.
            Respond ONLY with valid JSON and no markdown.

            Response format:
            {
              "reasoningSummary": "concise user-visible summary",
              "isComplete": false,
              "completionMessage": "",
              "action": {
                "objective": "what this single action accomplishes",
                "command": "one shell command",
                "requiresSafetyReview": true
              }
            }

            Set action to null when isComplete is true.

            Original objective:
            {{request.Objective}}

            Relevant chat history:
            {{request.ChatHistoryContext}}

            Current plan and progress:
            {{request.CurrentPlan}}

            Previous action result:
            {{request.PreviousActionResult ?? "No previous action result."}}

            Accumulated observations:
            {{AgentActionSession.BuildObservationContext(request.Observations)}}

            StepNumber: {{request.StepNumber}}
            RetryNumber: {{request.RetryNumber}}
            """;
    }

    private static void ValidateDecision(AgentActionDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.ReasoningSummary))
        {
            throw new ArgumentException("The ReAct decision did not include a reasoningSummary.");
        }

        decision.ReasoningSummary = TextTruncation.Truncate(
            decision.ReasoningSummary.Trim(),
            500);

        if (decision.IsComplete)
        {
            decision.Action = null;
            return;
        }

        if (!HasValidAction(decision.Action))
        {
            throw new ArgumentException("The ReAct decision did not include a valid action.");
        }

        decision.Action!.Objective = decision.Action.Objective.Trim();
        decision.Action.Command = decision.Action.Command.Trim();
    }

    private static bool HasValidAction(AgentToolAction? action)
    {
        return action is not null &&
               !string.IsNullOrWhiteSpace(action.Objective) &&
               !string.IsNullOrWhiteSpace(action.Command);
    }

    private static bool ToolResponseIndicatesFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var normalizedOutput = output.ToLowerInvariant();
        string[] failureSignals =
        [
            "is not recognized as an internal or external command",
            "command not found",
            "no such file or directory",
            "traceback (most recent call last)",
            "syntaxerror:",
            "permission denied"
        ];

        return failureSignals.Any(
            signal => normalizedOutput.Contains(signal, StringComparison.Ordinal));
    }

    internal static bool IsAffirmativeResponse(string rawResponse)
    {
        var response = ModelResponse.Parse(rawResponse).Response.Trim();
        return Regex.IsMatch(response, @"^yes\b", RegexOptions.IgnoreCase);
    }

    private sealed record DecisionAttempt(
        AgentActionDecision? Decision,
        string? Failure)
    {
        public bool Succeeded => Decision is not null;

        public static DecisionAttempt Success(AgentActionDecision decision)
        {
            return new DecisionAttempt(decision, Failure: null);
        }

        public static DecisionAttempt Failed(string failure)
        {
            return new DecisionAttempt(Decision: null, failure);
        }
    }

    private sealed record ActionAttemptResult(
        string Observation,
        bool RequiresRetry,
        ConversationTurn? TerminalTurn)
    {
        public static ActionAttemptResult Completed(string observation)
        {
            return new ActionAttemptResult(observation, RequiresRetry: false, TerminalTurn: null);
        }

        public static ActionAttemptResult Retry(string observation)
        {
            return new ActionAttemptResult(observation, RequiresRetry: true, TerminalTurn: null);
        }

        public static ActionAttemptResult Terminal(ConversationTurn turn)
        {
            return new ActionAttemptResult(string.Empty, RequiresRetry: false, turn);
        }
    }
}
