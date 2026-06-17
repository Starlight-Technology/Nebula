using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Agent.Domain;
using Nebula.Agent.Infrastructure;
using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Interactions;
using Nebula.Core.Learning;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Runner;
using Nebula.Services.Commands;
using Nebula.Services.Learning;
using Nebula.Services.Operations;
using Nebula.Services.Safety;

namespace Nebula.Agent;

public sealed class AgentActionRunner : IAgentActionRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILlamaClient llamaClient;
    private readonly IShellExecutor executor;
    private readonly IJsonExtractor jsonExtractor;
    private readonly ILogger logger;
    private readonly CommandValidationService commandValidationService;
    private readonly CommandAuditService commandAuditService;
    private readonly CommandDeduplication commandDeduplication = new();
    private readonly ICommandIntentParser commandIntentParser;
    private readonly ICommandResolver commandResolver;
    private readonly IRuntimeCommandEnvironmentDetector environmentDetector;
    private readonly IOperationKindDetector operationKindDetector;
    private readonly IScriptContentSafetyClassifier scriptContentClassifier;
    private readonly IFileWriteSafetyClassifier fileWriteSafetyClassifier;
    private readonly IOperationPolicyEngine operationPolicyEngine;
    private readonly IExecutionEvidenceCollector evidenceCollector;
    private readonly ILearningEngine learningEngine;
    private readonly IKnowledgeQueryService knowledgeQueryService;
    private readonly NebulaRuntimeSettings runtimeSettings;
    private readonly int defaultMaxRetriesPerStep;
    private readonly int defaultMaxSteps;

    public AgentActionRunner(
        ILlamaClient llamaClient,
        IShellExecutor executor,
        IJsonExtractor jsonExtractor,
        ILogger logger,
        ICommandRepository? commandRepository = null,
        int maxRetries = AgentActionRunRequest.DefaultMaxRetriesPerStep,
        int maxSteps = AgentActionRunRequest.DefaultMaxSteps,
        ICommandPolicyEngine? commandPolicyEngine = null,
        ICommandIntentParser? commandIntentParser = null,
        ICommandResolver? commandResolver = null,
        IRuntimeCommandEnvironmentDetector? environmentDetector = null,
        IOperationKindDetector? operationKindDetector = null,
        IScriptContentSafetyClassifier? scriptContentClassifier = null,
        IFileWriteSafetyClassifier? fileWriteSafetyClassifier = null,
        IOperationPolicyEngine? operationPolicyEngine = null,
        IExecutionEvidenceCollector? evidenceCollector = null,
        ILearningEngine? learningEngine = null,
        IKnowledgeQueryService? knowledgeQueryService = null,
        NebulaRuntimeSettings? runtimeSettings = null)
    {
        this.llamaClient = llamaClient;
        this.executor = executor;
        this.jsonExtractor = jsonExtractor;
        this.logger = logger;
        var effectiveCommandPolicy =
            commandPolicyEngine ?? CreateDefaultPolicyEngine(logger);
        commandValidationService = new CommandValidationService(
            llamaClient,
            effectiveCommandPolicy);
        commandAuditService = new CommandAuditService(commandRepository, logger);
        this.commandIntentParser =
            commandIntentParser ?? new CommandIntentParser();
        this.commandResolver =
            commandResolver ?? new CommandResolver();
        this.environmentDetector =
            environmentDetector ?? new RuntimeCommandEnvironmentDetector();
        this.operationKindDetector =
            operationKindDetector ?? new OperationKindDetector();
        this.fileWriteSafetyClassifier =
            fileWriteSafetyClassifier ?? new FileWriteSafetyClassifier();
        this.scriptContentClassifier =
            scriptContentClassifier ??
            new ScriptContentSafetyClassifier(this.fileWriteSafetyClassifier);
        this.operationPolicyEngine =
            operationPolicyEngine ??
            new OperationPolicyEngine(
                effectiveCommandPolicy,
                message => logger.Log($"[AGENT] {message}"));
        this.evidenceCollector =
            evidenceCollector ?? new ExecutionEvidenceCollector();
        this.runtimeSettings = runtimeSettings ?? new NebulaRuntimeSettings();
        var fallbackKnowledgeStore = new InMemoryKnowledgeStore();
        this.learningEngine =
            learningEngine ?? CreateDefaultLearningEngine(
                executor,
                effectiveCommandPolicy,
                fallbackKnowledgeStore);
        this.knowledgeQueryService =
            knowledgeQueryService ??
            new KnowledgeQueryService(fallbackKnowledgeStore, logger);
        defaultMaxRetriesPerStep = Math.Max(0, maxRetries);
        defaultMaxSteps = Math.Max(1, maxSteps);
    }

    public async Task<ConversationTurn> RunAsync(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        if (request.ApprovedAction is not null)
        {
            return await RunApprovedActionAsync(request, progress, cancellationToken);
        }

        if (TryExtractKnowledgeTopic(request.Prompt, out var knowledgeTopic))
        {
            return await RunKnowledgeQueryAsync(
                request,
                knowledgeTopic,
                cancellationToken);
        }

        var requestOperation = operationKindDetector.Detect(new AgentStep
        {
            SessionId = request.ConversationId,
            OriginalText = request.Prompt,
            Objective = request.Prompt,
            WorkingDirectory = Environment.CurrentDirectory
        });
        if (requestOperation is OperationKind.Learning or OperationKind.Research)
        {
            return await RunLearningAsync(request, cancellationToken);
        }

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

        var decisionPrompt = CreateDecisionPrompt(
            request,
            environmentDetector.Detect(Environment.CurrentDirectory),
            runtimeSettings.BuildResponseLanguageInstruction());
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

    private async Task<ConversationTurn> RunApprovedActionAsync(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken)
    {
        var approved = request.ApprovedAction
            ?? throw new InvalidOperationException("Approved action is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(approved.Command);

        var session = new AgentActionSession(
            request,
            progress,
            logger,
            llamaClient.SelectedModel,
            defaultMaxSteps,
            defaultMaxRetriesPerStep);
        session.EmitReasoning(
            "Executando um comando aprovado explicitamente pela interface.");

        var action = new AgentToolAction
        {
            Objective = string.IsNullOrWhiteSpace(approved.Objective)
                ? "Executar comando aprovado"
                : approved.Objective.Trim(),
            Command = approved.Command.Trim(),
            OperationKind = approved.OperationKind,
            TargetPath = approved.TargetPath,
            WorkingDirectory = approved.WorkingDirectory,
            RequiresSafetyReview = true
        };

        var actionResult = await ExecuteActionAsync(session, action, cancellationToken);
        if (actionResult.TerminalTurn is not null)
        {
            return actionResult.TerminalTurn;
        }

        if (actionResult.RequiresRetry)
        {
            return session.FailRetryLimit(actionResult.Observation);
        }

        return session.Complete(
            $"Comando aprovado executado. {actionResult.Observation}");
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
        if (session.TryTakeRecoveryDecision(out var recoveryDecision))
        {
            return DecisionAttempt.Success(recoveryDecision!);
        }

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
        var step = session.CreateStep(action);
        var operationKind = operationKindDetector.Detect(step);
        logger.Log(
            $"[AGENT] Operation detected: sessionId={step.SessionId}; stepId={step.Id}; " +
            $"operationKind={operationKind}; objective={step.Objective}");

        return operationKind switch
        {
            OperationKind.FileWrite or OperationKind.ScriptContent =>
                await ExecuteFileWriteAsync(
                    session,
                    action,
                    step,
                    operationKind,
                    cancellationToken),
            OperationKind.FileRead =>
                await ExecuteFileReadAsync(
                    session,
                    action,
                    step,
                    cancellationToken),
            OperationKind.TerminalCommand or OperationKind.ScriptExecution =>
                await ExecuteCommandActionAsync(
                    session,
                    action,
                    step,
                    operationKind,
                    cancellationToken),
            _ => ActionAttemptResult.Terminal(
                session.BlockUnsupportedOperation(operationKind))
        };
    }

    private async Task<ActionAttemptResult> ExecuteCommandActionAsync(
        AgentActionSession session,
        AgentToolAction action,
        AgentStep step,
        OperationKind operationKind,
        CancellationToken cancellationToken)
    {
        var execution = session.CreateExecution(action, step, operationKind);
        var environment = environmentDetector.Detect(execution.WorkingDirectory);
        var commandRequest = commandIntentParser.Parse(
            session.Request.Prompt,
            action.Command,
            execution.WorkingDirectory);
        var resolvedCommand = commandResolver.Resolve(commandRequest, environment);
        ApplyResolution(execution, resolvedCommand, environment);

        if (operationKind == OperationKind.ScriptExecution)
        {
            var artifactDecision = await EvaluateScriptArtifactAsync(
                session,
                action,
                execution,
                cancellationToken);
            if (artifactDecision.Decision != CommandSafetyDecisionType.Allow)
            {
                if (!TryApplyApprovalOverride(session, execution, artifactDecision))
                {
                    session.Commands.Add(execution);
                    return artifactDecision.Decision switch
                    {
                        CommandSafetyDecisionType.Block =>
                            ActionAttemptResult.Terminal(
                                session.BlockUnsafeCommand(execution)),
                        _ => ActionAttemptResult.Terminal(
                                session.RequestCommandApproval(execution))
                    };
                }
            }
        }

        var environmentSnapshot = ExecutionEnvironmentSnapshot.Capture(
            execution.WorkingDirectory);
        var storedCommand = await commandAuditService.SaveCommandAsync(
            session.Request.RequestId,
            execution,
            cancellationToken);
        var validation = await commandValidationService.ValidateAsync(
            execution,
            cancellationToken);

        var operationDecision = await operationPolicyEngine.EvaluateAsync(
            new OperationPolicyRequest(
                session.Request.ConversationId,
                execution.StepId,
                operationKind,
                session.Request.Prompt,
                execution.Run,
                execution.TargetPath,
                new CommandClassification(
                    execution.Run,
                    validation.SafetyDecision.Intent,
                    validation.SafetyDecision.Confidence,
                    "CommandSafetyClassifier",
                    validation.SafetyDecision.Reasons)),
            cancellationToken);
        var commandCorrect = session.Request.ApprovedAction is not null ||
                             validation.Correct;
        validation = new CommandValidation(commandCorrect, operationDecision);
        execution.IsCorrect = commandCorrect;
        execution.IsSafe =
            operationDecision.Decision == CommandSafetyDecisionType.Allow;
        execution.PassedLocalSafety = execution.IsSafe;
        execution.ClassificationSource = "CommandSafetyClassifier";
        execution.ClassificationConfidence = operationDecision.Confidence;
        execution.SafetyDecision = operationDecision.Decision;
        execution.Notes = BuildOperationVerificationNotes(
            execution,
            operationDecision);

        var approvalOverrideApplied =
            validation.SafetyDecision.Decision == CommandSafetyDecisionType.AskApproval &&
            TryApplyApprovalOverride(session, execution, validation.SafetyDecision);

        await commandAuditService.SaveVerificationAsync(
            storedCommand?.Id,
            execution,
            cancellationToken);
        session.Commands.Add(execution);

        LogPreExecution(
            session.Request.ConversationId,
            session.Request.Prompt,
            execution,
            validation.SafetyDecision);

        if (validation.SafetyDecision.Decision == CommandSafetyDecisionType.Block)
        {
            return ActionAttemptResult.Terminal(session.BlockUnsafeCommand(execution));
        }

        if (validation.SafetyDecision.Decision == CommandSafetyDecisionType.AskApproval &&
            !approvalOverrideApplied)
        {
            return ActionAttemptResult.Terminal(session.RequestCommandApproval(execution));
        }

        if (!validation.Correct)
        {
            var observation = execution.Notes
                ?? "The proposed action does not satisfy the current step.";
            session.RecordCommandObservation(execution.Run, observation);
            return ActionAttemptResult.Retry(observation);
        }

        var deduplication = commandDeduplication.Evaluate(
            execution.Run,
            execution.WorkingDirectory,
            environmentSnapshot,
            action.RetryJustification,
            session.ExecutionHistory);
        if (!deduplication.Allowed)
        {
            session.RecordDeduplicationBlocked(execution, deduplication.Reason);
            await commandAuditService.UpdateExecutionAsync(
                storedCommand?.Id,
                executed: false,
                deduplication.Reason,
                cancellationToken);
            return ActionAttemptResult.Retry(deduplication.Reason);
        }

        session.EmitActionStarted(execution);
        var toolResult = await ExecuteToolAsync(
            execution,
            resolvedCommand,
            cancellationToken);
        session.EmitActionCompleted(execution);

        var historyEntry = CreateHistoryEntry(
            execution,
            toolResult,
            environmentSnapshot);
        session.RecordExecution(historyEntry);
        session.RecordEvidence(evidenceCollector.Collect(
            new ExecutionEvidenceInput(
                session.Request.ConversationId,
                execution.StepId,
                operationKind,
                execution.Run,
                execution.TargetPath,
                Executed: true,
                ExitCode: toolResult.ExitCode,
                StdOut: toolResult.StandardOutput,
                StdErr: toolResult.StandardError,
                Success: toolResult.Success)));

        var observationMessage = BuildObservationMessage(execution);
        session.EmitToolObservation(execution, observationMessage, toolResult.CombinedOutput);
        session.RecordObservation(execution.Run, observationMessage);

        await commandAuditService.UpdateExecutionAsync(
            storedCommand?.Id,
            execution.Executed,
            execution.Executed ? toolResult.CombinedOutput : observationMessage,
            cancellationToken);

        if (execution.Executed)
        {
            return ActionAttemptResult.Completed(observationMessage);
        }

        var previousExecution = session.ExecutionHistory.Entries.Count >= 2
            ? session.ExecutionHistory.Entries[^2]
            : null;
        if (previousExecution is
            {
                Success: false,
                ErrorSignature: "command-not-found"
            })
        {
            logger.Log(
                "[AGENT] Command-not-found alternative failed; stopping automatic retries: " +
                $"original={previousExecution.Command}; alternative={execution.Run}; " +
                $"alternativeError={historyEntry.ErrorSignature}");
            return ActionAttemptResult.Terminal(
                session.FailCommandNotFoundAlternative(
                    previousExecution,
                    historyEntry));
        }

        if (historyEntry.ErrorSignature == "command-not-found")
        {
            logger.Log(
                $"[AGENT] Command-not-found failure recorded: command={execution.Run}; " +
                "at most one compatible alternative may be attempted.");
        }

        var reflection = await TryReflectOnFailureAsync(
            session,
            execution,
            cancellationToken);
        session.RevisePlan(execution, reflection);

        var similarFailureCount = session.ExecutionHistory.CountFailures(
            historyEntry.ErrorSignature);
        if (similarFailureCount >= 3)
        {
            return ActionAttemptResult.Terminal(
                session.FailRepeatedError(historyEntry, similarFailureCount));
        }

        return ActionAttemptResult.Retry(observationMessage);
    }

    private async Task<ActionAttemptResult> ExecuteFileWriteAsync(
        AgentActionSession session,
        AgentToolAction action,
        AgentStep step,
        OperationKind operationKind,
        CancellationToken cancellationToken)
    {
        var execution = session.CreateExecution(action, step, operationKind);
        var content = action.Content ?? string.Empty;
        var targetPath = ResolveOperationPath(
            action.TargetPath,
            execution.WorkingDirectory);
        execution.TargetPath = targetPath;
        execution.Run = $"write-file \"{targetPath}\"";
        execution.OriginalCommand = action.Command;

        var classification = operationKind == OperationKind.ScriptContent
            ? scriptContentClassifier.Classify(
                content,
                action.Language ?? string.Empty,
                targetPath)
            : fileWriteSafetyClassifier.Classify(targetPath);
        var decision = await operationPolicyEngine.EvaluateAsync(
            new OperationPolicyRequest(
                session.Request.ConversationId,
                execution.StepId,
                operationKind,
                session.Request.Prompt,
                ResolvedCommand: null,
                targetPath,
                classification),
            cancellationToken);
        ApplyOperationClassification(execution, classification, decision);
        session.Commands.Add(execution);

        if (decision.Decision == CommandSafetyDecisionType.Block)
        {
            return ActionAttemptResult.Terminal(
                session.BlockUnsafeCommand(execution));
        }

        if (decision.Decision == CommandSafetyDecisionType.AskApproval &&
            !TryApplyApprovalOverride(session, execution, decision))
        {
            return ActionAttemptResult.Terminal(
                session.RequestCommandApproval(execution));
        }

        session.EmitActionStarted(execution);
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(targetPath) ??
                execution.WorkingDirectory);
            await File.WriteAllTextAsync(
                targetPath,
                content,
                Encoding.UTF8,
                cancellationToken);
            execution.Executed = true;
            execution.ExitCode = 0;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.StandardOutput = $"File written: {targetPath}";
            execution.Output = execution.StandardOutput;
            execution.Notes = "File content was written successfully.";

            var evidence = evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    operationKind,
                    FilePath: targetPath,
                    Content: content,
                    Executed: true,
                    ExitCode: 0,
                    StdOut: execution.StandardOutput,
                    Success: true));
            execution.ContentHash = evidence.ContentHash;
            session.RecordEvidence(evidence);
            if (operationKind == OperationKind.ScriptContent)
            {
                session.RecordArtifact(
                    targetPath,
                    evidence.ContentHash ?? string.Empty,
                    classification);
            }

            session.EmitActionCompleted(execution);
            var observation =
                $"File path: {targetPath}{Environment.NewLine}" +
                $"Content hash: {evidence.ContentHash}{Environment.NewLine}" +
                "Success: True";
            session.EmitToolObservation(
                execution,
                observation,
                execution.StandardOutput);
            session.RecordObservation(execution.Run, observation);
            return ActionAttemptResult.Completed(observation);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            execution.Executed = false;
            execution.ExitCode = -1;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.StandardError = ex.Message;
            execution.Error = ex.Message;
            execution.Notes = $"File write failed: {ex.Message}";
            session.RecordEvidence(evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    operationKind,
                    FilePath: targetPath,
                    Content: content,
                    Executed: true,
                    ExitCode: -1,
                    StdErr: ex.Message,
                    Success: false)));
            session.EmitActionCompleted(execution);
            session.RecordObservation(execution.Run, execution.Notes);
            return ActionAttemptResult.Retry(execution.Notes);
        }
    }

    private async Task<ActionAttemptResult> ExecuteFileReadAsync(
        AgentActionSession session,
        AgentToolAction action,
        AgentStep step,
        CancellationToken cancellationToken)
    {
        var execution = session.CreateExecution(
            action,
            step,
            OperationKind.FileRead);
        var targetPath = ResolveOperationPath(
            action.TargetPath,
            execution.WorkingDirectory);
        execution.TargetPath = targetPath;
        execution.Run = $"read-file \"{targetPath}\"";

        var classification = ClassifyFileRead(
            targetPath,
            execution.WorkingDirectory);
        var decision = await operationPolicyEngine.EvaluateAsync(
            new OperationPolicyRequest(
                session.Request.ConversationId,
                execution.StepId,
                OperationKind.FileRead,
                session.Request.Prompt,
                ResolvedCommand: null,
                targetPath,
                classification),
            cancellationToken);
        ApplyOperationClassification(execution, classification, decision);
        session.Commands.Add(execution);

        if (decision.Decision == CommandSafetyDecisionType.Block)
        {
            return ActionAttemptResult.Terminal(
                session.BlockUnsafeCommand(execution));
        }

        if (decision.Decision == CommandSafetyDecisionType.AskApproval &&
            !TryApplyApprovalOverride(session, execution, decision))
        {
            return ActionAttemptResult.Terminal(
                session.RequestCommandApproval(execution));
        }

        session.EmitActionStarted(execution);
        try
        {
            var content = await File.ReadAllTextAsync(
                targetPath,
                cancellationToken);
            execution.Executed = true;
            execution.ExitCode = 0;
            execution.StandardOutput = content;
            execution.Output = content;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            var evidence = evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    OperationKind.FileRead,
                    FilePath: targetPath,
                    Content: content,
                    Executed: true,
                    ExitCode: 0,
                    StdOut: content,
                    Success: true));
            execution.ContentHash = evidence.ContentHash;
            session.RecordEvidence(evidence);
            session.EmitActionCompleted(execution);
            var observation =
                $"Read {content.Length} character(s) from {targetPath}.";
            session.EmitToolObservation(execution, observation, content);
            session.RecordObservation(execution.Run, observation);
            return ActionAttemptResult.Completed(observation);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            execution.Executed = false;
            execution.ExitCode = -1;
            execution.StandardError = ex.Message;
            execution.Error = ex.Message;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            session.RecordEvidence(evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    OperationKind.FileRead,
                    FilePath: targetPath,
                    Executed: true,
                    ExitCode: -1,
                    StdErr: ex.Message,
                    Success: false)));
            session.EmitActionCompleted(execution);
            return ActionAttemptResult.Retry(ex.Message);
        }
    }

    private async Task<ShellCommandResult> ExecuteToolAsync(
        CommandExecution execution,
        ResolvedCommand resolvedCommand,
        CancellationToken cancellationToken)
    {
        try
        {
            ShellCommandResult result;
            if (executor is IResolvedCommandExecutor resolvedExecutor)
            {
                result = await resolvedExecutor.RunCommandDetailedAsync(
                    resolvedCommand,
                    cancellationToken);
            }
            else if (executor is IDetailedShellExecutor detailedExecutor)
            {
                result = await detailedExecutor.RunCommandDetailedAsync(
                    execution.Run,
                    execution.WorkingDirectory,
                    cancellationToken);
            }
            else
            {
                var legacyOutput = await executor.RunCommandAsync(
                    execution.Run,
                    cancellationToken);
                var failed = ToolResponseIndicatesFailure(legacyOutput);
                result = new ShellCommandResult
                {
                    Command = execution.Run,
                    WorkingDirectory = execution.WorkingDirectory,
                    StandardOutput = failed ? string.Empty : legacyOutput,
                    StandardError = failed ? legacyOutput : string.Empty,
                    ExitCode = failed ? 1 : 0,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

            ApplyToolResult(execution, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var result = new ShellCommandResult
            {
                Command = execution.Run,
                WorkingDirectory = execution.WorkingDirectory,
                StandardError = ex.Message,
                ExitCode = -1,
                Timestamp = DateTimeOffset.UtcNow
            };
            ApplyToolResult(execution, result);
            return result;
        }
    }

    private static void ApplyResolution(
        CommandExecution execution,
        ResolvedCommand resolvedCommand,
        RuntimeCommandEnvironment environment)
    {
        execution.Run = resolvedCommand.DisplayCommand;
        execution.ResolvedFileName = resolvedCommand.FileName;
        execution.ResolvedArguments = resolvedCommand.Arguments;
        execution.WorkingDirectory = resolvedCommand.WorkingDirectory;
        execution.OperatingSystem = environment.OS;
        execution.Shell = environment.Shell;
        execution.ResolutionReasons = resolvedCommand.Reasons;
    }

    private void LogPreExecution(
        Guid sessionId,
        string userText,
        CommandExecution execution,
        CommandSafetyDecision safetyDecision)
    {
        logger.Log(
            "[AGENT] Command execution decision: " +
            $"sessionId={sessionId}; stepId={execution.StepId}; " +
            $"operationKind={execution.OperationKind}; " +
            $"os={execution.OperatingSystem}; shell={execution.Shell}; " +
            $"userText={userText}; originalCommand={execution.OriginalCommand}; " +
            $"resolvedCommand={execution.Run}; fileName={execution.ResolvedFileName}; " +
            $"arguments={execution.ResolvedArguments}; workingDirectory={execution.WorkingDirectory}; " +
            $"intent={safetyDecision.Intent}; riskLevel={ToRiskLevel(safetyDecision)}; " +
            $"confidence={safetyDecision.Confidence:F3}; source={execution.ClassificationSource}; " +
            $"policyDecision={safetyDecision.Decision}; " +
            $"policyReasons={string.Join(" | ", safetyDecision.Reasons)}; " +
            $"resolutionReasons={string.Join(" | ", execution.ResolutionReasons)}");
    }

    private static CommandRiskLevel ToRiskLevel(
        CommandSafetyDecision decision) =>
        decision.Decision switch
        {
            CommandSafetyDecisionType.Allow => CommandRiskLevel.Low,
            CommandSafetyDecisionType.AskApproval => CommandRiskLevel.Medium,
            _ => CommandRiskLevel.Critical
        };

    private static void ApplyToolResult(
        CommandExecution execution,
        ShellCommandResult result)
    {
        execution.StandardOutput = result.StandardOutput;
        execution.StandardError = result.StandardError;
        execution.ExitCode = result.ExitCode;
        execution.ExecutedAt = result.Timestamp;
        execution.Output = result.CombinedOutput;
        execution.Executed = result.Success;

        if (!result.Success)
        {
            execution.Error = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"Command exited with code {result.ExitCode}."
                : result.StandardError.Trim();
            execution.Notes =
                $"Falha ao executar o comando (exit code {result.ExitCode}). " +
                $"{execution.Error}";
            return;
        }

        execution.Notes = string.IsNullOrWhiteSpace(result.CombinedOutput)
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

    private static string BuildObservationMessage(CommandExecution execution)
    {
        return $$"""
            Command: {{execution.Run}}
            Working directory: {{execution.WorkingDirectory}}
            Exit code: {{execution.ExitCode?.ToString() ?? "unknown"}}
            Success: {{execution.Executed}}
            Stdout: {{FormatOutput(execution.StandardOutput)}}
            Stderr: {{FormatOutput(execution.StandardError)}}
            """;
    }

    private async Task<ErrorReflection?> TryReflectOnFailureAsync(
        AgentActionSession session,
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        try
        {
            var decisionRequest = session.CreateDecisionRequest();
            var reflectionPrompt = CreateErrorReflectionPrompt(
                execution,
                decisionRequest.CurrentPlan,
                session.ExecutionHistory.Entries,
                runtimeSettings.BuildResponseLanguageInstruction());
            var rawResponse = await llamaClient.GetResponseAsync(
                reflectionPrompt,
                progress: null,
                cancellationToken);
            var responsePayload = ModelResponse.Parse(rawResponse).Response;
            var json = ExtractJsonObject(responsePayload);
            var reflection = JsonSerializer.Deserialize<ErrorReflection>(json, JsonOptions)
                ?? throw new JsonException("The model returned an empty error reflection.");
            ValidateReflection(reflection, execution.Run);
            return reflection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError($"[AGENT] Unable to reflect on failed command: {ex.Message}");
            return null;
        }
    }

    private static ExecutionHistoryEntry CreateHistoryEntry(
        CommandExecution execution,
        ShellCommandResult result,
        ExecutionEnvironmentSnapshot environmentSnapshot)
    {
        return new ExecutionHistoryEntry
        {
            Command = execution.Run,
            WorkingDirectory = execution.WorkingDirectory,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            ExitCode = result.ExitCode,
            Success = result.Success,
            Timestamp = result.Timestamp,
            EnvironmentFingerprint = environmentSnapshot.EnvironmentFingerprint,
            FileFingerprint = environmentSnapshot.FileFingerprint,
            ErrorSignature = result.Success
                ? string.Empty
                : ExecutionHistory.CreateErrorSignature(
                    result.StandardOutput,
                    result.StandardError,
                    result.ExitCode)
        };
    }

    private static string CreateErrorReflectionPrompt(
        CommandExecution execution,
        string currentPlan,
        IReadOnlyList<ExecutionHistoryEntry> history,
        string responseLanguageInstruction)
    {
        return $$"""
            You are Nebula's ErrorReflectionStep.

            Observe the real command result before proposing another action.
            The next command must be different from the failed command.
            Diagnose the likely cause from stdout, stderr and exit code.
            Revise the failed plan step instead of continuing the old plan blindly.
            {{responseLanguageInstruction}}
            Respond ONLY with valid JSON and no markdown.

            Response format:
            {
              "hypothesis": "probable cause grounded in the error",
              "alternativeAction": "diagnostic or corrective action",
              "nextCommand": "a different shell command"
            }

            Command executed:
            {{execution.Run}}

            Working directory:
            {{execution.WorkingDirectory}}

            Stdout:
            {{FormatOutput(execution.StandardOutput)}}

            Stderr:
            {{FormatOutput(execution.StandardError)}}

            Exit code:
            {{execution.ExitCode?.ToString() ?? "unknown"}}

            Current plan:
            {{currentPlan}}

            Recent execution history:
            {{ExecutionHistory.BuildContext(history)}}

            Mandatory question:
            Qual hipótese explica esse erro e qual comando diferente deve ser tentado agora?
            """;
    }

    private static void ValidateReflection(
        ErrorReflection reflection,
        string failedCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reflection.Hypothesis);
        ArgumentException.ThrowIfNullOrWhiteSpace(reflection.AlternativeAction);
        ArgumentException.ThrowIfNullOrWhiteSpace(reflection.NextCommand);

        reflection.Hypothesis = reflection.Hypothesis.Trim();
        reflection.AlternativeAction = reflection.AlternativeAction.Trim();
        reflection.NextCommand = reflection.NextCommand.Trim();

        if (CommandDeduplication.NormalizeCommand(reflection.NextCommand) ==
            CommandDeduplication.NormalizeCommand(failedCommand))
        {
            throw new ArgumentException(
                "The error reflection repeated the failed command instead of choosing a different one.");
        }
    }

    private static string FormatOutput(string? output)
    {
        return string.IsNullOrWhiteSpace(output)
            ? "(empty)"
            : TextTruncation.Truncate(output.Trim(), 4000);
    }

    private async Task<ConversationTurn> RunLearningAsync(
        AgentActionRunRequest request,
        CancellationToken cancellationToken)
    {
        var learningObjective = StripLearningSourceBlocks(request.Prompt);
        var sourceFilePaths = ExtractLearningSourceBlock(
            request.Prompt,
            "learning_source_files");
        var sourceUrls = ExtractLearningSourceBlock(
            request.Prompt,
            "learning_source_sites");
        var report = await learningEngine.LearnAsync(
            new LearningRequest(
                learningObjective,
                InferKnowledgeDomain(learningObjective),
                SourceFilePaths: sourceFilePaths,
                SourceUrls: sourceUrls),
            cancellationToken);
        var response = report.Success
            ? BuildLearningReport(report)
            : report.Error ?? "Learning failed without a diagnostic.";

        return new ConversationTurn
        {
            ConversationId = request.ConversationId,
            RequestId = request.RequestId,
            Prompt = request.Prompt,
            Mode = InteractionMode.Agent,
            ModelName = string.IsNullOrWhiteSpace(request.ModelName)
                ? llamaClient.SelectedModel
                : request.ModelName,
            Classification = InteractionMode.Agent.ToString(),
            Response = response,
            Reasoning = report.Success
                ? "Learning report is based on configured research sources, stored items, and safe experiment results."
                : response,
            ActionStatus = report.Success
                ? ActionExecutionStatus.Completed
                : ActionExecutionStatus.Failed
        };
    }

    private static IReadOnlyList<string> ExtractLearningSourceBlock(
        string prompt,
        string blockName)
    {
        var pattern =
            $@"\[{Regex.Escape(blockName)}\](?<content>.*?)\[/\s*{Regex.Escape(blockName)}\]";
        var match = Regex.Match(
            prompt,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return [];
        }

        return match.Groups["content"].Value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('-', '*').Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string StripLearningSourceBlocks(string prompt)
    {
        var value = Regex.Replace(
            prompt,
            @"\[(?:learning_source_files|learning_source_sites)\].*?\[/\s*(?:learning_source_files|learning_source_sites)\]",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return string.IsNullOrWhiteSpace(value)
            ? prompt.Trim()
            : value.Trim();
    }

    private ILearningEngine CreateDefaultLearningEngine(
        IShellExecutor shellExecutor,
        ICommandPolicyEngine commandPolicyEngine,
        IKnowledgeStore knowledgeStore)
    {
        var deterministicExtractor = new KnowledgeExtractor();
        return new LearningEngine(
            new DisabledWebResearchService(),
            new LlamaKnowledgeExtractor(
                llamaClient,
                jsonExtractor,
                runtimeSettings,
                deterministicExtractor,
                message => logger.Log($"[AGENT] {message}")),
            new KnowledgeClassificationPipeline(
                log: message => logger.Log($"[AGENT] {message}")),
            knowledgeStore,
            new SafeExperimentRunner(
                shellExecutor,
                commandPolicyEngine,
                commandIntentParser,
                commandResolver,
                environmentDetector,
                scriptContentClassifier),
            new KnowledgeScoreEngine(),
            logger);
    }

    private async Task<ConversationTurn> RunKnowledgeQueryAsync(
        AgentActionRunRequest request,
        string topic,
        CancellationToken cancellationToken)
    {
        var response = await knowledgeQueryService.AnswerAsync(
            topic,
            cancellationToken);
        return new ConversationTurn
        {
            ConversationId = request.ConversationId,
            RequestId = request.RequestId,
            Prompt = request.Prompt,
            Mode = InteractionMode.Agent,
            ModelName = string.IsNullOrWhiteSpace(request.ModelName)
                ? llamaClient.SelectedModel
                : request.ModelName,
            Classification = InteractionMode.Agent.ToString(),
            Response = response,
            Reasoning =
                "Response retrieved from stored knowledge with source, score, date, and verification metadata.",
            ActionStatus = ActionExecutionStatus.Completed
        };
    }

    private async Task<CommandSafetyDecision> EvaluateScriptArtifactAsync(
        AgentActionSession session,
        AgentToolAction action,
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        var scriptPath = TryExtractScriptPath(
            action.TargetPath,
            action.Command,
            execution.WorkingDirectory);
        execution.TargetPath = scriptPath;

        CommandClassification classification;
        if (string.IsNullOrWhiteSpace(scriptPath) ||
            !session.TryGetArtifact(scriptPath, out var artifact) ||
            artifact is null)
        {
            classification = new CommandClassification(
                execution.Run,
                CommandIntent.NeedsApproval,
                0.99,
                "SessionArtifactPolicy",
                [
                    "Script execution is allowed automatically only for a file created and classified as safe in this agent session."
                ]);
        }
        else
        {
            classification = artifact.Classification with
            {
                CommandText = execution.Run,
                Source = "SessionArtifactPolicy",
                Reasons =
                [
                    .. artifact.Classification.Reasons,
                    "The script was created by the agent in this session and its content hash is recorded."
                ]
            };
            execution.ContentHash = artifact.ContentHash;
        }

        var decision = await operationPolicyEngine.EvaluateAsync(
            new OperationPolicyRequest(
                session.Request.ConversationId,
                execution.StepId,
                OperationKind.ScriptExecution,
                session.Request.Prompt,
                execution.Run,
                scriptPath,
                classification),
            cancellationToken);
        ApplyOperationClassification(execution, classification, decision);
        return decision;
    }

    private static string ResolveOperationPath(
        string? targetPath,
        string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(targetPath)
            ? Path.GetFullPath(targetPath)
            : Path.GetFullPath(targetPath, workingDirectory);
    }

    private static string? TryExtractScriptPath(
        string? targetPath,
        string command,
        string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            return ResolveOperationPath(targetPath, workingDirectory);
        }

        var match = Regex.Match(
            command,
            @"(?<path>(?:[a-z]:[\\/])?[^""'\s;&|]+\.(?:py|ps1|bat|cmd|csproj|sln))",
            RegexOptions.IgnoreCase);
        return match.Success
            ? ResolveOperationPath(match.Groups["path"].Value, workingDirectory)
            : null;
    }

    private static CommandClassification ClassifyFileRead(
        string targetPath,
        string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new CommandClassification(
                targetPath,
                CommandIntent.Blocked,
                1,
                "FileReadSafetyClassifier",
                ["A file read requires a target path."]);
        }

        var sensitive = new[]
        {
            ".env", ".ssh", "id_rsa", "id_ed25519", "credentials",
            "access_token", "auth_token", "api_key", "password"
        };
        if (sensitive.Any(value =>
            targetPath.Contains(value, StringComparison.OrdinalIgnoreCase)))
        {
            return new CommandClassification(
                targetPath,
                CommandIntent.DataExfiltration,
                0.99,
                "FileReadSafetyClassifier",
                ["Reading credential, token, .env, or SSH material is blocked."]);
        }

        var root = Path.GetFullPath(workingDirectory);
        var relative = Path.GetRelativePath(root, targetPath);
        var inside = relative != ".." &&
                     !relative.StartsWith(
                         $"..{Path.DirectorySeparatorChar}",
                         StringComparison.Ordinal) &&
                     !Path.IsPathRooted(relative);
        return inside
            ? new CommandClassification(
                targetPath,
                CommandIntent.SafeReadOnly,
                0.99,
                "FileReadSafetyClassifier",
                ["The non-sensitive file is inside the active workspace."])
            : new CommandClassification(
                targetPath,
                CommandIntent.NeedsApproval,
                0.99,
                "FileReadSafetyClassifier",
                ["Reading a file outside the active workspace requires approval."]);
    }

    private static void ApplyOperationClassification(
        CommandExecution execution,
        CommandClassification classification,
        CommandSafetyDecision decision)
    {
        execution.IsCorrect = true;
        execution.IsSafe = decision.Decision == CommandSafetyDecisionType.Allow;
        execution.PassedLocalSafety = execution.IsSafe;
        execution.ClassificationSource = classification.Source;
        execution.ClassificationConfidence = classification.Confidence;
        execution.SafetyDecision = decision.Decision;
        execution.Notes = BuildOperationVerificationNotes(execution, decision);
    }

    private bool TryApplyApprovalOverride(
        AgentActionSession session,
        CommandExecution execution,
        CommandSafetyDecision decision)
    {
        if (decision.Decision != CommandSafetyDecisionType.AskApproval ||
            !IsApprovalOverridableOperation(execution.OperationKind))
        {
            return false;
        }

        var approvedByUser = session.Request.ApprovedAction is not null;
        var autoApproved = runtimeSettings.AutoApproveCommands;
        if (!approvedByUser && !autoApproved)
        {
            return false;
        }

        execution.ApprovedByUser = approvedByUser;
        execution.AutoApproved = !approvedByUser && autoApproved;
        execution.SafetyDecision = decision.Decision;
        execution.Notes = AppendApprovalNote(
            execution.Notes,
            execution.AutoApproved
                ? "Aprovado automaticamente pelas preferencias do runtime."
                : "Aprovado manualmente pela interface.");
        session.EmitApprovalGranted(execution);
        return true;
    }

    private static bool IsApprovalOverridableOperation(OperationKind operationKind) =>
        operationKind is OperationKind.TerminalCommand or OperationKind.ScriptExecution;

    private static string AppendApprovalNote(string? notes, string approvalNote) =>
        string.IsNullOrWhiteSpace(notes)
            ? approvalNote
            : $"{notes}; {approvalNote}";

    private static string BuildOperationVerificationNotes(
        CommandExecution execution,
        CommandSafetyDecision decision) =>
        $"operationKind={execution.OperationKind}; decision={decision.Decision}; " +
        $"intent={decision.Intent}; confidence={decision.Confidence:F3}; " +
        $"reasons={string.Join(" | ", decision.Reasons)}";

    private static KnowledgeDomain InferKnowledgeDomain(string prompt)
    {
        var normalized = prompt.ToLowerInvariant();
        if (normalized.Contains("powershell"))
        {
            return KnowledgeDomain.PowerShell;
        }

        if (normalized.Contains("shell") ||
            normalized.Contains("seguran") ||
            normalized.Contains("sandbox") ||
            normalized.Contains("comando"))
        {
            return KnowledgeDomain.ShellSecurity;
        }

        if (normalized.Contains("windows"))
        {
            return KnowledgeDomain.WindowsCommands;
        }

        if (normalized.Contains("linux") || normalized.Contains("bash"))
        {
            return KnowledgeDomain.LinuxCommands;
        }

        if (normalized.Contains("python"))
        {
            return KnowledgeDomain.Python;
        }

        if (normalized.Contains(".net") || normalized.Contains("dotnet"))
        {
            return KnowledgeDomain.DotNet;
        }

        if (normalized.Contains("matem"))
        {
            return KnowledgeDomain.Mathematics;
        }

        if (normalized.Contains("fisic") || normalized.Contains("físic"))
        {
            return KnowledgeDomain.Physics;
        }

        if (normalized.Contains("quim") || normalized.Contains("quím"))
        {
            return KnowledgeDomain.Chemistry;
        }

        return KnowledgeDomain.General;
    }

    private static bool TryExtractKnowledgeTopic(
        string prompt,
        out string topic)
    {
        var match = KnowledgeQuestionRegex.Match(prompt.Trim());
        topic = match.Success
            ? match.Groups["topic"].Value.Trim().TrimEnd('?', '.', '!')
            : string.Empty;
        return !string.IsNullOrWhiteSpace(topic);
    }

    private static string BuildLearningReport(LearningReport report)
    {
        const int sampleLimit = 20;
        var sourceNames = report.Sources
            .Select(source => string.IsNullOrWhiteSpace(source.ProviderName)
                ? source.Publisher
                : source.ProviderName)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var commandItems = report.Items
            .Where(item => item.Kind == KnowledgeItemKind.Command)
            .ToList();
        var conceptItems = report.Items
            .Where(item => item.Kind != KnowledgeItemKind.Command)
            .ToList();
        var domainSummary = report.Items
            .GroupBy(item => item.Domain)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .Select(group => $"{group.Key}: {group.Count()}")
            .ToList();
        var commandExamples = commandItems
            .Select(item => string.IsNullOrWhiteSpace(item.NormalizedCommand)
                ? item.Title.Replace("CMD:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()
                : item.NormalizedCommand)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var sampleItems = report.Items.Take(sampleLimit).ToList();
        var lines = new List<string>
        {
            $"Aprendi {report.Items.Count} itens usando fontes {string.Join(", ", sourceNames.DefaultIfEmpty("configuradas"))}.",
            $"Criados: {report.CreatedCount}. Atualizados: {report.UpdatedCount}. Ignorados: {report.SkippedCount}.",
            $"Documentos encontrados: {report.DocumentsFound}. Fontes registradas: {report.Sources.Count}.",
            $"Itens perigosos identificados: {report.DangerousCount}.",
            $"Resumo do que aprendi: {commandItems.Count} comandos e {conceptItems.Count} outros itens.",
            $"Dominios: {string.Join(", ", domainSummary.DefaultIfEmpty("sem dominio"))}."
        };
        if (commandExamples.Count > 0)
        {
            lines.Add($"Exemplos de comandos aprendidos: {string.Join(", ", commandExamples)}.");
        }

        if (report.Warnings is { Count: > 0 })
        {
            lines.Add("Warnings:");
            lines.AddRange(report.Warnings.Select(warning => $"- {warning}"));
        }

        lines.Add("Amostra do que foi salvo:");
        lines.Add(
            report.Items.Count > sampleLimit
                ? $"Mostrando {sampleItems.Count} de {report.Items.Count} itens aprendidos."
                : $"Mostrando {sampleItems.Count} itens aprendidos.");
        lines.Add($"Fatos armazenados: {report.Facts?.Count ?? 0}");
        lines.AddRange(sampleItems.Select(FormatLearnedItem));
        if (report.Items.Count > sampleItems.Count)
        {
            lines.Add(
                $"Mais {report.Items.Count - sampleItems.Count} itens ficaram salvos na base de conhecimento.");
        }

        if (report.ProviderDiagnostics is { Count: > 0 })
        {
            lines.Add("Providers consultados:");
            lines.AddRange(report.ProviderDiagnostics.Select(diagnostic =>
                $"- {diagnostic.ProviderName}: " +
                $"{(diagnostic.IsConfigured ? "enabled" : "disabled")}; " +
                $"{diagnostic.DocumentsFound} documents"));
        }

        var notTestable = report.Experiments.Count(value =>
            value.VerificationKind == VerificationKind.NotTestableLocally);
        if (notTestable > 0)
        {
            lines.Add($"Itens nao testaveis localmente: {notTestable}");
        }

        lines.Add(
            "Proximos passos: consultar a base de conhecimento antes de planejar comandos e manter regras deterministicas como autoridade final.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatLearnedItem(KnowledgeItem item)
    {
        var command = string.IsNullOrWhiteSpace(item.NormalizedCommand)
            ? string.Empty
            : $" [{item.NormalizedCommand}]";
        var tags = string.IsNullOrWhiteSpace(item.Tags)
            ? "sem tags"
            : item.Tags;
        return
            $"- {item.Title}{command}: {item.Summary} " +
            $"score {item.FinalScore:F2}; risco {item.RiskLevel}; tags {tags}";
    }

    private static readonly Regex KnowledgeQuestionRegex = new(
        @"^(?:o\s+que\s+(?:voc[eê]\s+)?sabe\s+sobre|consulte\s+(?:a\s+)?base\s+(?:de\s+conhecimento\s+)?sobre|qual\s+(?:é|e)\s+o\s+conhecimento\s+sobre)\s+(?<topic>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private string ExtractJsonObject(string input)
    {
        try
        {
            return jsonExtractor.ExtractJsonObject(input);
        }
        catch (ArgumentException ex)
        {
            logger.LogError($"[AGENT] Error extracting JSON: {ex.Message}");
            throw;
        }
    }

    private static string CreateDecisionPrompt(
        AgentActionDecisionRequest request,
        RuntimeCommandEnvironment environment,
        string responseLanguageInstruction)
    {
        return $$"""
            Você está em AGENT MODE.

            O usuário espera que a tarefa seja executada.
            Não responda apenas com explicações.
            Crie um plano e execute uma etapa por vez.
            Colete evidências reais de cada ferramenta.
            Relate somente resultados observados.
            Se algo não puder ser executado, informe claramente.
            Não invente resultados e não afirme sucesso sem evidência.

            You are Nebula's ReAct action controller.

            Choose exactly one next action, or declare the objective complete.
            Select an operationKind before providing action data.
            For FileWrite or ScriptContent, provide content, targetPath, and language. Do not put source code in command.
            For TerminalCommand or ScriptExecution, provide command. ScriptExecution must target a file created earlier in this session.
            Valid operation kinds are TerminalCommand, FileWrite, FileRead, ScriptContent, ScriptExecution, Research, and Learning.
            The runtime is {{environment.OS}} using {{environment.Shell}}. Propose commands compatible with that OS and shell.
            Do not propose Unix-only commands such as ls, rm, chmod, chown, grep or cat on Windows.
            Never reveal chain-of-thought, hidden reasoning, or private analysis.
            The reasoningSummary must be a concise user-visible summary of the next practical need, at most two sentences.
            {{responseLanguageInstruction}}
            Use the previous action result and accumulated observations to correct failures.
            When RetryNumber is greater than zero, correct the same logical step instead of silently skipping it.
            Você é um agente executor. Você deve observar o resultado real de cada comando antes de agir novamente.
            Se um comando falhar, não repita o mesmo comando.
            Primeiro explique a causa provável do erro.
            Depois escolha uma ação diferente.
            Repetir comando só é permitido se algo mudou no ambiente ou se houver justificativa explícita.
            Se o mesmo erro ocorrer 3 vezes, pare e peça intervenção humana.
            Environment changes include a different working directory, relevant file changes, environment variable changes or changed command arguments. Put an explicit retry explanation in retryJustification.
            Do not claim completion unless the observations demonstrate that the objective is complete.
            Respond ONLY with valid JSON and no markdown.

            Response format:
            {
              "reasoningSummary": "concise user-visible summary",
              "isComplete": false,
              "completionMessage": "",
              "action": {
                "objective": "what this single action accomplishes",
                "operationKind": "TerminalCommand|FileWrite|FileRead|ScriptContent|ScriptExecution",
                "command": "one shell command, empty for file content operations",
                "content": "exact file or script content, null for terminal commands",
                "targetPath": "target file path when applicable",
                "language": "python|csharp|json|markdown|text when applicable",
                "workingDirectory": "optional absolute or relative directory",
                "retryJustification": "required only for a justified repeat of a failed command",
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

            Recent execution history:
            {{ExecutionHistory.BuildContext(request.ExecutionHistory)}}

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
        decision.Action.Content = decision.Action.Content?.Trim();
        decision.Action.TargetPath = decision.Action.TargetPath?.Trim();
        decision.Action.Language = decision.Action.Language?.Trim();
    }

    private static bool HasValidAction(AgentToolAction? action)
    {
        return action is not null &&
               !string.IsNullOrWhiteSpace(action.Objective) &&
               (!string.IsNullOrWhiteSpace(action.Command) ||
                (!string.IsNullOrWhiteSpace(action.Content) &&
                 !string.IsNullOrWhiteSpace(action.TargetPath)));
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

    private static ICommandPolicyEngine CreateDefaultPolicyEngine(ILogger logger)
    {
        var deterministic = new DeterministicCommandClassifier();
        var ml = new MlNetCommandClassifier();
        var composite = new CompositeCommandClassifier(deterministic, ml);
        return new CommandPolicyEngine(
            composite,
            message => logger.Log($"[AGENT] {message}"));
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
