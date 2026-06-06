using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Nebula.Agent.Data;
using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.Agent;

public sealed class AgentActionRunner(
    ILlamaClient llamaClient,
    IShellExecutor executor,
    IJsonExtractor jsonExtractor,
    ILogger logger,
    ICommandRepository? commandRepository = null,
    int maxRetries = AgentActionRunRequest.DefaultMaxRetriesPerStep,
    int maxSteps = AgentActionRunRequest.DefaultMaxSteps) : IAgentActionRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly int defaultMaxRetriesPerStep = Math.Max(0, maxRetries);
    private readonly int defaultMaxSteps = Math.Max(1, maxSteps);

    public async Task<ConversationTurn> RunAsync(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var maxStepCount = Math.Max(1, request.MaxSteps ?? defaultMaxSteps);
#pragma warning disable CS0618
        var maxRetryCount = Math.Max(
            0,
            request.MaxRetriesPerStep ??
            request.MaxRetries ??
            defaultMaxRetriesPerStep);
#pragma warning restore CS0618

        var events = new List<ActionExecutionEvent>();
        var commands = new List<CommandExecution>();
        var observations = new List<string>();
        var completedPlanSteps = new List<string>();
        string? previousActionResult = null;
        var stepNumber = 1;
        var retryNumber = 0;

        try
        {
            EmitEvent(
                request,
                progress,
                events,
                commands,
                ActionExecutionEventKind.ReasoningSummary,
                ActionExecutionStatus.Validating,
                stepNumber,
                retryNumber + 1,
                "Reasoning summary",
                "I need to validate the objective before using local tools.");

            var requestValidation = await ValidateAsync(request, cancellationToken);
            if (!requestValidation.IsValid)
            {
                var response = $"A acao foi bloqueada antes de executar ferramentas. Motivo: {requestValidation.Reason}";
                EmitTerminalEvent(
                    request,
                    progress,
                    events,
                    commands,
                    ActionExecutionEventKind.Unsafe,
                    ActionExecutionStatus.Unsafe,
                    stepNumber,
                    retryNumber + 1,
                    "Unsafe",
                    response);

                return BuildTurn(
                    request,
                    ActionExecutionStatus.Unsafe,
                    response,
                    commands,
                    events);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AgentActionDecision decision;
                try
                {
                    decision = await GenerateNextStepAsync(new AgentActionDecisionRequest
                    {
                        Objective = request.Prompt,
                        ChatHistoryContext = request.ChatHistoryContext,
                        CurrentPlan = BuildCurrentPlan(completedPlanSteps),
                        PreviousActionResult = previousActionResult,
                        Observations = observations.ToList(),
                        StepNumber = stepNumber,
                        RetryNumber = retryNumber
                    }, cancellationToken);
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException)
                {
                    var observation = $"The next-action decision was invalid: {ex.Message}";
                    observations.Add(BuildObservationRecord(stepNumber, retryNumber + 1, "decision", observation));
                    previousActionResult = observation;

                    EmitEvent(
                        request,
                        progress,
                        events,
                        commands,
                        ActionExecutionEventKind.ReasoningSummary,
                        ActionExecutionStatus.Planning,
                        stepNumber,
                        retryNumber + 1,
                        "Reasoning summary",
                        "I could not produce a valid next action, so I need to correct the decision.");
                    EmitEvent(
                        request,
                        progress,
                        events,
                        commands,
                        ActionExecutionEventKind.Observation,
                        ActionExecutionStatus.Planning,
                        stepNumber,
                        retryNumber + 1,
                        "Observation",
                        observation,
                        error: ex.Message);

                    if (!TryScheduleRetry(
                            request,
                            progress,
                            events,
                            commands,
                            stepNumber,
                            ref retryNumber,
                            maxRetryCount,
                            observation))
                    {
                        return BuildRetryLimitFailure(
                            request,
                            progress,
                            events,
                            commands,
                            stepNumber,
                            retryNumber + 1,
                            maxRetryCount,
                            observation);
                    }

                    continue;
                }

                EmitEvent(
                    request,
                    progress,
                    events,
                    commands,
                    ActionExecutionEventKind.ReasoningSummary,
                    ActionExecutionStatus.Planning,
                    stepNumber,
                    retryNumber + 1,
                    "Reasoning summary",
                    decision.ReasoningSummary);

                if (decision.IsComplete)
                {
                    var response = string.IsNullOrWhiteSpace(decision.CompletionMessage)
                        ? "Objetivo concluido com sucesso."
                        : decision.CompletionMessage.Trim();

                    EmitTerminalEvent(
                        request,
                        progress,
                        events,
                        commands,
                        ActionExecutionEventKind.Completed,
                        ActionExecutionStatus.Completed,
                        stepNumber,
                        retryNumber + 1,
                        "Completed",
                        response);

                    return BuildTurn(
                        request,
                        ActionExecutionStatus.Completed,
                        response,
                        commands,
                        events);
                }

                if (stepNumber > maxStepCount)
                {
                    var response =
                        $"Nao consegui concluir a acao antes do limite de {maxStepCount} passo(s).";
                    EmitTerminalEvent(
                        request,
                        progress,
                        events,
                        commands,
                        ActionExecutionEventKind.Failed,
                        ActionExecutionStatus.Failed,
                        stepNumber,
                        retryNumber + 1,
                        "Failed",
                        response);

                    return BuildTurn(
                        request,
                        ActionExecutionStatus.Failed,
                        response,
                        commands,
                        events);
                }

                var action = decision.Action!;
                var execution = new CommandExecution
                {
                    Attempt = retryNumber + 1,
                    Id = stepNumber,
                    Objective = action.Objective,
                    Run = action.Command,
                    Required = true
                };

                var storedCommand = await TrySaveCommandAsync(
                    request.RequestId,
                    execution,
                    cancellationToken);

                var validation = await ValidateCommandAsync(execution, cancellationToken);
                await TrySaveVerificationAsync(
                    storedCommand?.Id,
                    execution,
                    cancellationToken);

                if (!validation.Safe)
                {
                    commands.Add(execution);
                    var response =
                        $"A acao foi interrompida porque o passo {stepNumber} ficou inseguro. {execution.Notes}";
                    EmitTerminalEvent(
                        request,
                        progress,
                        events,
                        commands,
                        ActionExecutionEventKind.Unsafe,
                        ActionExecutionStatus.Unsafe,
                        stepNumber,
                        retryNumber + 1,
                        "Unsafe",
                        response,
                        command: execution.Run);

                    return BuildTurn(
                        request,
                        ActionExecutionStatus.Unsafe,
                        response,
                        commands,
                        events);
                }

                if (!validation.Correct)
                {
                    commands.Add(execution);
                    var observation = execution.Notes ?? "The proposed action does not satisfy the current step.";
                    observations.Add(BuildObservationRecord(
                        stepNumber,
                        retryNumber + 1,
                        execution.Run,
                        observation));
                    previousActionResult = observation;

                    EmitEvent(
                        request,
                        progress,
                        events,
                        commands,
                        ActionExecutionEventKind.Observation,
                        ActionExecutionStatus.Executing,
                        stepNumber,
                        retryNumber + 1,
                        "Observation",
                        observation,
                        command: execution.Run);

                    if (!TryScheduleRetry(
                            request,
                            progress,
                            events,
                            commands,
                            stepNumber,
                            ref retryNumber,
                            maxRetryCount,
                            observation))
                    {
                        return BuildRetryLimitFailure(
                            request,
                            progress,
                            events,
                            commands,
                            stepNumber,
                            retryNumber + 1,
                            maxRetryCount,
                            observation);
                    }

                    continue;
                }

                commands.Add(execution);
                EmitEvent(
                    request,
                    progress,
                    events,
                    commands,
                    ActionExecutionEventKind.ActionStarted,
                    ActionExecutionStatus.Executing,
                    stepNumber,
                    retryNumber + 1,
                    "Action started",
                    action.Objective,
                    command: action.Command);

                string toolResponse;
                try
                {
                    toolResponse = await executor.RunCommandAsync(action.Command, cancellationToken);
                    execution.Output = toolResponse;

                    if (ToolResponseIndicatesFailure(toolResponse))
                    {
                        execution.Error = "Tool response indicated failure.";
                        execution.Notes = "Falha detectada ao inspecionar a resposta da ferramenta.";
                    }
                    else
                    {
                        execution.Executed = true;
                        execution.Notes = string.IsNullOrWhiteSpace(toolResponse)
                            ? "Comando executado sem saida textual."
                            : "Comando executado com sucesso.";
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    toolResponse = ex.Message;
                    execution.Error = ex.Message;
                    execution.Notes = $"Falha ao executar o comando: {ex.Message}";
                }

                EmitEvent(
                    request,
                    progress,
                    events,
                    commands,
                    ActionExecutionEventKind.ActionCompleted,
                    ActionExecutionStatus.Executing,
                    stepNumber,
                    retryNumber + 1,
                    "Action completed",
                    execution.Executed
                        ? "The existing terminal tool completed the action."
                        : "The existing terminal tool completed with a failure.",
                    command: action.Command,
                    error: execution.Error);

                var observationMessage = execution.Executed
                    ? (string.IsNullOrWhiteSpace(toolResponse)
                        ? "The tool completed successfully without textual output."
                        : toolResponse)
                    : execution.Notes ?? toolResponse;

                EmitEvent(
                    request,
                    progress,
                    events,
                    commands,
                    ActionExecutionEventKind.Observation,
                    ActionExecutionStatus.Executing,
                    stepNumber,
                    retryNumber + 1,
                    "Observation",
                    observationMessage,
                    command: action.Command,
                    toolResponse: toolResponse,
                    error: execution.Error);

                observations.Add(BuildObservationRecord(
                    stepNumber,
                    retryNumber + 1,
                    action.Command,
                    observationMessage));
                previousActionResult = observationMessage;

                await TryUpdateExecutionAsync(
                    storedCommand?.Id,
                    execution.Executed,
                    execution.Executed ? toolResponse : execution.Notes,
                    cancellationToken);

                if (!execution.Executed)
                {
                    if (!TryScheduleRetry(
                            request,
                            progress,
                            events,
                            commands,
                            stepNumber,
                            ref retryNumber,
                            maxRetryCount,
                            observationMessage))
                    {
                        return BuildRetryLimitFailure(
                            request,
                            progress,
                            events,
                            commands,
                            stepNumber,
                            retryNumber + 1,
                            maxRetryCount,
                            observationMessage);
                    }

                    continue;
                }

                completedPlanSteps.Add(
                    $"{stepNumber}. {action.Objective} - completed. Observation: {Truncate(observationMessage, 500)}");
                stepNumber++;
                retryNumber = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string response = "Execucao cancelada pelo usuario.";
            EmitTerminalEvent(
                request,
                progress,
                events,
                commands,
                ActionExecutionEventKind.Cancelled,
                ActionExecutionStatus.Cancelled,
                stepNumber,
                retryNumber + 1,
                "Cancelled",
                response);

            return BuildTurn(
                request,
                ActionExecutionStatus.Cancelled,
                response,
                commands,
                events,
                isCancelled: true);
        }
    }

    public async Task<AgentActionDecision> GenerateNextStepAsync(
        AgentActionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Objective);

        var payloadPrompt = $$"""
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
            {{BuildObservationContext(request.Observations)}}

            StepNumber: {{request.StepNumber}}
            RetryNumber: {{request.RetryNumber}}
            """;

        var rawResponse = await llamaClient.GetResponseAsync(
            payloadPrompt,
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

        var validationText = ShouldUseConversationContextForValidation(request.Prompt)
            ? $"{request.ChatHistoryContext}{Environment.NewLine}{request.Prompt}"
            : request.Prompt;
        var safe = IsActionTextSafe(validationText);
        var allowed = IsActionAllowed(validationText);
        var feasible = !string.IsNullOrWhiteSpace(request.Prompt) &&
                       !PlatformDetector.GetCurrentOsType().Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                       LooksLikeComputerOperationPrompt(request.Prompt);

        var failures = new List<string>();

        if (!safe)
        {
            failures.Add("a solicitacao contem padroes destrutivos ou perigosos");
        }

        if (!allowed)
        {
            failures.Add("a solicitacao viola a politica local de acoes permitidas");
        }

        if (!feasible)
        {
            failures.Add("a acao nao parece tecnicamente executavel neste ambiente");
        }

        return Task.FromResult(new ActionValidationResult
        {
            Safe = safe,
            Allowed = allowed,
            Feasible = feasible,
            Reason = failures.Count == 0
                ? "A acao foi considerada segura, permitida e tecnicamente viavel."
                : string.Join("; ", failures)
        });
    }

    private async Task<CommandValidation> ValidateCommandAsync(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        execution.IsCorrect = await VerifyCommandCorrectAsync(execution, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        execution.IsSafe = await VerifyCommandSafetyAsync(execution, cancellationToken);
        execution.PassedLocalSafety = PlatformDetector.IsCommandContentSafe(execution.Run);
        execution.Notes = BuildVerificationNotes(execution);

        return new CommandValidation(
            Correct: execution.IsCorrect,
            Safe: execution.IsSafe && execution.PassedLocalSafety);
    }

    private async Task<bool> VerifyCommandCorrectAsync(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        var response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". Does this command execute exactly the objective on {{PlatformDetector.GetCurrentOsType()}}?
            Objective: {{execution.Objective}}
            Command: {{execution.Run}}
            """,
            progress: null,
            cancellationToken);

        return IsAffirmativeResponse(response);
    }

    private async Task<bool> VerifyCommandSafetyAsync(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        var response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". Is this command safe and allowed to execute on {{PlatformDetector.GetCurrentOsType()}}?
            Command: {{execution.Run}}
            """,
            progress: null,
            cancellationToken);

        return IsAffirmativeResponse(response);
    }

    private bool TryScheduleRetry(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        List<ActionExecutionEvent> events,
        IReadOnlyList<CommandExecution> commands,
        int stepNumber,
        ref int retryNumber,
        int maxRetryCount,
        string failure)
    {
        if (retryNumber >= maxRetryCount)
        {
            return false;
        }

        retryNumber++;
        EmitEvent(
            request,
            progress,
            events,
            commands,
            ActionExecutionEventKind.RetryScheduled,
            ActionExecutionStatus.Retrying,
            stepNumber,
            retryNumber + 1,
            "Retry scheduled",
            $"Retry {retryNumber} of {maxRetryCount}. Previous observation: {failure}");
        return true;
    }

    private ConversationTurn BuildRetryLimitFailure(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        List<ActionExecutionEvent> events,
        IReadOnlyList<CommandExecution> commands,
        int stepNumber,
        int attempt,
        int maxRetryCount,
        string failure)
    {
        var response =
            $"Nao consegui concluir o passo {stepNumber}. Limite de retry por passo ({maxRetryCount}) atingido. Motivo: {failure}";
        EmitTerminalEvent(
            request,
            progress,
            events,
            commands,
            ActionExecutionEventKind.Failed,
            ActionExecutionStatus.Failed,
            stepNumber,
            attempt,
            "Failed",
            response);

        return BuildTurn(
            request,
            ActionExecutionStatus.Failed,
            response,
            commands,
            events);
    }

    private ConversationTurn BuildTurn(
        AgentActionRunRequest request,
        ActionExecutionStatus status,
        string response,
        IReadOnlyList<CommandExecution> commands,
        IReadOnlyList<ActionExecutionEvent> events,
        bool isCancelled = false)
    {
        return new ConversationTurn
        {
            ConversationId = request.ConversationId,
            RequestId = request.RequestId,
            Prompt = request.Prompt,
            ModelName = string.IsNullOrWhiteSpace(request.ModelName)
                ? llamaClient.SelectedModel
                : request.ModelName,
            Classification = status == ActionExecutionStatus.Cancelled
                ? ActionExecutionStatus.Cancelled.ToString()
                : ClassificationResult.Action.ToString(),
            Response = response,
            Reasoning = BuildVisibleReasoning(events),
            Commands = commands.ToList(),
            ActionStatus = status,
            ActionEvents = events.ToList(),
            IsCancelled = isCancelled
        };
    }

    private void EmitTerminalEvent(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        List<ActionExecutionEvent> events,
        IReadOnlyList<CommandExecution> commands,
        ActionExecutionEventKind kind,
        ActionExecutionStatus status,
        int step,
        int attempt,
        string title,
        string message,
        string? command = null)
    {
        EmitEvent(
            request,
            progress,
            events,
            commands,
            kind,
            status,
            step,
            attempt,
            title,
            message,
            command);
    }

    private void EmitEvent(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        List<ActionExecutionEvent> events,
        IReadOnlyList<CommandExecution> commands,
        ActionExecutionEventKind kind,
        ActionExecutionStatus status,
        int step,
        int attempt,
        string title,
        string message,
        string? command = null,
        string? toolResponse = null,
        string? error = null)
    {
        var actionEvent = new ActionExecutionEvent
        {
            Kind = kind,
            Status = status,
            Step = Math.Max(1, step),
            Attempt = Math.Max(1, attempt),
            Title = title,
            Message = message,
            Command = command,
            ToolResponse = toolResponse,
            Error = error
        };

        events.Add(actionEvent);
        logger.Log(
            $"ReAct event [{kind}] step {actionEvent.Step} attempt {actionEvent.Attempt}: {message}");

        progress?.Report(new ConversationTurn
        {
            ConversationId = request.ConversationId,
            RequestId = request.RequestId,
            Prompt = request.Prompt,
            ModelName = string.IsNullOrWhiteSpace(request.ModelName)
                ? llamaClient.SelectedModel
                : request.ModelName,
            Classification = status == ActionExecutionStatus.Cancelled
                ? ActionExecutionStatus.Cancelled.ToString()
                : ClassificationResult.Action.ToString(),
            Response = message,
            Reasoning = BuildVisibleReasoning(events),
            Commands = commands.ToList(),
            ActionStatus = status,
            ActionEvents = events.ToList(),
            IsCancelled = status == ActionExecutionStatus.Cancelled
        });
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

    private async Task<StoredCommand?> TrySaveCommandAsync(
        Guid requestId,
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        if (commandRepository is null)
        {
            return null;
        }

        try
        {
            return await commandRepository.SaveAsync(new StoredCommand
            {
                RequestId = requestId,
                CommandId = execution.Id,
                Objective = execution.Objective,
                Command = execution.Run,
                OsType = PlatformDetector.GetCurrentOsType()
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to persist command '{execution.Run}': {ex.Message}");
            return null;
        }
    }

    private async Task TrySaveVerificationAsync(
        Guid? storedCommandId,
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        if (commandRepository is null || storedCommandId is null)
        {
            return;
        }

        try
        {
            await commandRepository.SaveVerificationAsync(new CommandVerification
            {
                CommandId = storedCommandId.Value,
                IsCorrect = execution.IsCorrect,
                IsSafe = execution.IsSafe && execution.PassedLocalSafety,
                VerificationNotes = BuildVerificationNotes(execution)
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to persist verification for command '{storedCommandId}': {ex.Message}");
        }
    }

    private async Task TryUpdateExecutionAsync(
        Guid? storedCommandId,
        bool executed,
        string? result,
        CancellationToken cancellationToken)
    {
        if (commandRepository is null || storedCommandId is null)
        {
            return;
        }

        try
        {
            await commandRepository.UpdateExecutionAsync(
                storedCommandId.Value,
                executed,
                result,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to update execution for command '{storedCommandId}': {ex.Message}");
        }
    }

    private static void ValidateDecision(AgentActionDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision.ReasoningSummary))
        {
            throw new ArgumentException("The ReAct decision did not include a reasoningSummary.");
        }

        decision.ReasoningSummary = Truncate(decision.ReasoningSummary.Trim(), 500);

        if (decision.IsComplete)
        {
            decision.Action = null;
            return;
        }

        if (decision.Action is null ||
            string.IsNullOrWhiteSpace(decision.Action.Objective) ||
            string.IsNullOrWhiteSpace(decision.Action.Command))
        {
            throw new ArgumentException("The ReAct decision did not include a valid action.");
        }

        decision.Action.Objective = decision.Action.Objective.Trim();
        decision.Action.Command = decision.Action.Command.Trim();
    }

    private static string BuildCurrentPlan(IReadOnlyList<string> completedPlanSteps)
    {
        return completedPlanSteps.Count == 0
            ? "No actions completed yet."
            : string.Join(Environment.NewLine, completedPlanSteps);
    }

    private static string BuildObservationContext(IReadOnlyList<string> observations)
    {
        if (observations.Count == 0)
        {
            return "No observations yet.";
        }

        var value = string.Join(Environment.NewLine, observations);
        return value.Length <= 16000
            ? value
            : value[^16000..];
    }

    private static string BuildObservationRecord(
        int step,
        int attempt,
        string action,
        string observation)
    {
        return
            $"Step {step}, attempt {attempt}, action `{action}`: " +
            Truncate(observation, 2000);
    }

    private static string BuildVisibleReasoning(IReadOnlyList<ActionExecutionEvent> events)
    {
        var builder = new StringBuilder();

        foreach (var actionEvent in events)
        {
            builder.AppendLine(
                $"[{actionEvent.Kind}] Step {actionEvent.Step}, attempt {actionEvent.Attempt}: " +
                actionEvent.Message);

            if (!string.IsNullOrWhiteSpace(actionEvent.Command))
            {
                builder.AppendLine($"Action: {actionEvent.Command}");
            }

            if (!string.IsNullOrWhiteSpace(actionEvent.ToolResponse))
            {
                builder.AppendLine($"Observation: {actionEvent.ToolResponse.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(actionEvent.Error))
            {
                builder.AppendLine($"Error: {actionEvent.Error}");
            }
        }

        return builder.ToString().Trim();
    }

    private static bool ToolResponseIndicatesFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var normalized = output.ToLowerInvariant();
        string[] failureSignals =
        [
            "is not recognized as an internal or external command",
            "command not found",
            "no such file or directory",
            "traceback (most recent call last)",
            "syntaxerror:",
            "permission denied"
        ];

        return failureSignals.Any(signal => normalized.Contains(signal, StringComparison.Ordinal));
    }

    private static bool LooksLikeComputerOperationPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var normalized = prompt.Trim().ToLowerInvariant();
        string[] actionKeywords =
        [
            "arquivo", "arquivos", "pasta", "pastas", "diretorio", "diretorios",
            "terminal", "comando", "comandos", "shell", "powershell", "bash", "cmd",
            "git", "docker", "script", "scripts", "repositorio", "repo", "rodar",
            "executar", "criar", "listar", "abrir", "instalar", "remover", "deletar",
            "apagar", "mover", "copiar", "renomear", "editar", "salvar", "alterar",
            "altere", "atualizar", "atualize", "mudar", "mude", "run ", "execute",
            "create", "list ", "open ", "install", "remove", "delete", "move ", "copy ",
            "rename", "edit ", "save ", "change", "update", "file", "files", "folder",
            "directory"
        ];

        return actionKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.Ordinal));
    }

    private static bool ShouldUseConversationContextForValidation(string userRequest)
    {
        var normalized = userRequest.ToLowerInvariant();
        string[] referentialTerms =
        [
            "that", "it", "previous", "above", "same", "isso", "aquilo", "anterior",
            "mesmo", "mesma", "ele", "ela"
        ];

        return referentialTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    private static bool IsActionTextSafe(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !PlatformDetector.IsCommandContentSafe(value))
        {
            return false;
        }

        var normalized = value.ToLowerInvariant();
        string[] unsafePhrases =
        [
            "delete system files", "apagar arquivos do sistema", "deletar arquivos do sistema",
            "format c:", "format disk", "wipe disk", "wipe the disk", "erase the disk",
            "del /s c:\\", "system32", "mkfs", "dd if=", "cipher /w"
        ];

        return !unsafePhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }

    private static bool IsActionAllowed(string value)
    {
        var normalized = value.ToLowerInvariant();
        string[] disallowedPhrases =
        [
            "steal", "exfiltrate", "keylogger", "ransomware", "malware",
            "credential theft", "roubar senha", "disable antivirus", "desativar antivirus"
        ];

        return !disallowedPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal));
    }

    private static string BuildVerificationNotes(CommandExecution execution)
    {
        if (execution.IsCorrect && execution.IsSafe && execution.PassedLocalSafety)
        {
            return "Aprovado pela verificacao do modelo e pela protecao local.";
        }

        var failures = new List<string>();

        if (!execution.IsCorrect)
        {
            failures.Add("o modelo nao confirmou que o comando atende ao objetivo");
        }

        if (!execution.IsSafe)
        {
            failures.Add("o modelo nao considerou o comando seguro");
        }

        if (!execution.PassedLocalSafety)
        {
            failures.Add("a protecao local bloqueou um padrao de comando perigoso");
        }

        return $"Passo bloqueado porque {string.Join("; ", failures)}.";
    }

    private static bool IsAffirmativeResponse(string rawResponse)
    {
        var response = ModelResponse.Parse(rawResponse).Response.Trim();
        return Regex.IsMatch(response, @"^yes\b", RegexOptions.IgnoreCase);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength].Trim();
    }

    private sealed record CommandValidation(bool Correct, bool Safe);
}
