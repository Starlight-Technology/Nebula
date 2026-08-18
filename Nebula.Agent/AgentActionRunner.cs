using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Agent.Domain;
using Nebula.Agent.Infrastructure;
using Nebula.Core.Agent;
using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Core.Interactions;
using Nebula.Core.Learning;
using Nebula.Core.Memory;
using Nebula.Core.Operations;
using Nebula.Core.Projects;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Runner;
using Nebula.Services.Commands;
using Nebula.Services.Learning;
using Nebula.Services.Operations;
using Nebula.Services.Projects;
using Nebula.Services.Safety;

namespace Nebula.Agent;

public sealed class AgentActionRunner : IAgentActionRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonObject DecisionJsonSchema = BuildDecisionJsonSchema();

    private static JsonObject BuildDecisionJsonSchema()
    {
        var operationKinds = new JsonArray();
        foreach (var kind in Enum.GetValues<OperationKind>())
        {
            if (kind != OperationKind.Unknown)
            {
                operationKinds.Add(kind.ToString());
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["reasoningSummary"] = new JsonObject { ["type"] = "string" },
                ["isComplete"] = new JsonObject { ["type"] = "boolean" },
                ["completionMessage"] = new JsonObject { ["type"] = "string" },
                ["plan"] = new JsonObject
                {
                    ["type"] = new JsonArray("array", "null"),
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject { ["type"] = "integer" },
                            ["description"] = new JsonObject { ["type"] = "string" },
                            ["dependsOn"] = new JsonObject
                            {
                                ["type"] = new JsonArray("array", "null"),
                                ["items"] = new JsonObject { ["type"] = "integer" }
                            },
                            ["status"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray("pending", "inProgress", "completed")
                            }
                        },
                        ["required"] = new JsonArray("id", "description")
                    }
                },
                ["action"] = new JsonObject
                {
                    ["type"] = new JsonArray("object", "null"),
                    ["properties"] = new JsonObject
                    {
                        ["objective"] = new JsonObject { ["type"] = "string" },
                        ["command"] = new JsonObject { ["type"] = "string" },
                        ["operationKind"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = operationKinds
                        },
                        ["content"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                        ["targetPath"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                        ["templateId"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                        ["plannedFiles"] = new JsonObject
                        {
                            ["type"] = new JsonArray("array", "null"),
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["path"] = new JsonObject { ["type"] = "string" },
                                    ["content"] = new JsonObject { ["type"] = "string" }
                                },
                                ["required"] = new JsonArray("path", "content")
                            }
                        },
                        ["language"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                        ["workingDirectory"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                        ["retryJustification"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                        ["requiresSafetyReview"] = new JsonObject { ["type"] = "boolean" }
                    },
                    ["required"] = new JsonArray("objective", "command", "operationKind")
                }
            },
            ["required"] = new JsonArray("reasoningSummary", "isComplete", "completionMessage")
        };
    }

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
    private readonly ProjectScaffoldSafetyClassifier scaffoldClassifier = new();
    private readonly IOperationPolicyEngine operationPolicyEngine;
    private readonly IExecutionEvidenceCollector evidenceCollector;
    private readonly ILearningEngine learningEngine;
    private readonly IKnowledgeQueryService knowledgeQueryService;
    private readonly ILearningFromExecutionService? learningFromExecution;
    private readonly IPostTaskLearningService? postTaskLearning;
    private readonly IOutputVerificationService? outputVerificationService;
    private readonly ITranslationService? translationService;
    private readonly IAgentRunStore? agentRunStore;
    private readonly IDeterministicVerificationService? deterministicVerification;
    private readonly IProjectTemplateCatalog? projectTemplateCatalog;
    private readonly IProjectScaffolder? projectScaffolder;
    private readonly IProjectStackValidator? projectStackValidator;
    private readonly IWorkspaceMapService? workspaceMapService;
    private readonly IPlannedPatchApplier? plannedPatchApplier;
    private readonly IGitDiffService? gitDiffService;
    private readonly ICommandSandbox? commandSandbox;
    private readonly NebulaRuntimeSettings runtimeSettings;
    private readonly ICommandApprovalService approvalService;
    private readonly WorkspaceMemoryService? workspaceMemoryService;
    private readonly ICommandAllowlistService? commandAllowlistService;
    private readonly IWorkspaceCategoryPolicyService? workspaceCategoryPolicyService;
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
        ILearningFromExecutionService? learningFromExecution = null,
        IPostTaskLearningService? postTaskLearning = null,
        IOutputVerificationService? outputVerificationService = null,
        ITranslationService? translationService = null,
        NebulaRuntimeSettings? runtimeSettings = null,
        IAgentRunStore? agentRunStore = null,
        IDeterministicVerificationService? deterministicVerificationService = null,
        IProjectTemplateCatalog? projectTemplateCatalog = null,
        IProjectScaffolder? projectScaffolder = null,
        IProjectStackValidator? projectStackValidator = null,
        IWorkspaceMapService? workspaceMapService = null,
        IPlannedPatchApplier? plannedPatchApplier = null,
        IGitDiffService? gitDiffService = null,
        ICommandSandbox? commandSandbox = null,
        ICommandApprovalService? approvalService = null,
        WorkspaceMemoryService? workspaceMemoryService = null,
        ICommandAllowlistService? commandAllowlistService = null,
        IWorkspaceCategoryPolicyService? workspaceCategoryPolicyService = null)
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
        this.learningFromExecution = learningFromExecution;
        this.postTaskLearning = postTaskLearning;
        this.outputVerificationService = outputVerificationService;
        this.translationService = translationService;
        this.agentRunStore = agentRunStore;
        this.deterministicVerification = deterministicVerificationService;
        this.projectTemplateCatalog = projectTemplateCatalog;
        this.projectScaffolder = projectScaffolder;
        this.projectStackValidator = projectStackValidator;
        this.workspaceMapService = workspaceMapService;
        this.plannedPatchApplier = plannedPatchApplier;
        this.gitDiffService = gitDiffService;
        this.commandSandbox = commandSandbox;
        this.approvalService = approvalService ?? new CommandApprovalService();
        this.workspaceMemoryService = workspaceMemoryService;
        this.commandAllowlistService = commandAllowlistService;
        this.workspaceCategoryPolicyService = workspaceCategoryPolicyService;
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
            WorkingDirectory = ReferenceWorkspace.Resolve(request.WorkspaceRoot).Root
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
            var turn = await RunCoreAsync(session, cancellationToken);
            await EnrichTurnWithGitDiffAsync(turn, session, cancellationToken);
            await PersistRunAsync(request, turn, cancellationToken);
            return turn;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = session.Cancel();
            await PersistRunAsync(request, cancelled, CancellationToken.None, isFinal: false);
            return cancelled;
        }
#if DEBUG
        catch (Exception ex)
        {
            logger.Log($"[AGENT] Debug exception: {ex.GetType().Name}: {ex.Message}");
            var details = $"**Erro no agente:** {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
            return new ConversationTurn
            {
                ConversationId = session.Request.ConversationId,
                RequestId = session.Request.RequestId,
                Prompt = session.Request.Prompt,
                Mode = InteractionMode.Agent,
                ModelName = llamaClient.SelectedModel,
                Response = details,
                ActionStatus = ActionExecutionStatus.Failed,
                ActionEvents = session.Events,
                Evidence = session.Evidence,
                IsCancelled = false
            };
        }
#endif
    }

    public async Task<AgentActionDecision> GenerateNextStepAsync(
        AgentActionDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Objective);

        var knowledgeContext = await QueryRelevantKnowledgeAsync(
            request.Objective,
            cancellationToken);
        var workspaceRoot = ReferenceWorkspace.Resolve(request.WorkspaceRoot);
        var workspaceContext = await BuildWorkspaceContextAsync(
            workspaceRoot.Root,
            cancellationToken);
        var templateContext = BuildTemplateContext();
        var decisionPrompt = CreateDecisionPrompt(
            request,
            environmentDetector.Detect(workspaceRoot.Root),
            runtimeSettings.BuildResponseLanguageInstruction(),
            knowledgeContext,
            workspaceContext,
            templateContext);
        var rawResponse = await llamaClient.GetStructuredResponseAsync(
            decisionPrompt,
            DecisionJsonSchema,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            throw new JsonException("The model returned an empty response.");
        }

        LogRawResponse(rawResponse, request);
        var responsePayload = ModelResponse.Parse(rawResponse).Response;
        var json = SanitizeJson(ExtractJsonObject(responsePayload));
        var decision = JsonSerializer.Deserialize<AgentActionDecision>(json, JsonOptions);

        if (decision is null)
        {
            throw new JsonException(
                $"The model returned an empty ReAct decision. Raw: {TruncateForError(rawResponse)}");
        }

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
        if (approved.OperationKind == OperationKind.PlannedPatch)
        {
            if (approved.PlannedFiles is not { Count: > 0 })
            {
                throw new ArgumentException(
                    "An approved planned patch requires at least one file.");
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(approved.Command);
        }

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
            PlannedFiles = approved.PlannedFiles,
            Content = approved.Content,
            Language = approved.Language,
            TemplateId = approved.TemplateId,
            WorkingDirectory = approved.WorkingDirectory,
            RequiresSafetyReview = true
        };

var actionResult = await ExecuteActionAsync(session, action, cancellationToken);
        ConversationTurn result;
        if (actionResult.TerminalTurn is not null)
        {
            result = actionResult.TerminalTurn;
        }
        else if (actionResult.RequiresRetry)
        {
            result = session.FailRetryLimit(actionResult.Observation);
        }
        else
        {
            result = session.Complete(
                $"Comando aprovado executado. {actionResult.Observation}");
        }

        await PersistRunAsync(request, result, cancellationToken);
        return result;
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
        var verificationFailures = 0;

        await TranslateObjectiveToEnglish(session, cancellationToken);

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
                    return await TranslateTurnResponseAsync(terminalTurn, cancellationToken);
                }

                continue;
            }

            var decision = decisionAttempt.Decision!;
            var translatedReasoning = await TranslateToResponseLanguageAsync(
                decision.ReasoningSummary, cancellationToken);
            session.EmitReasoning(translatedReasoning);
            session.ApplyPlan(decision.Plan);

            if (decision.IsComplete)
            {
                var verificationFailure = await VerifyCompletionDeterministicallyAsync(
                    session,
                    cancellationToken);
                if (verificationFailure is not null)
                {
                    session.RecordObservation("deterministic verification", verificationFailure);
                    var repairLimitExceeded =
                        runtimeSettings.MaxVerificationRetries > 0 &&
                        verificationFailures >= runtimeSettings.MaxVerificationRetries;
                    if (repairLimitExceeded)
                    {
                        var repairFailure =
                            $"A verificacao deterministica falhou mais de " +
                            $"{runtimeSettings.MaxVerificationRetries} vezes seguidas. " +
                            "Limite de correcoes apos falha de verificacao atingido. " +
                            $"Ultima falha: {verificationFailure}";
                        return await TranslateTurnResponseAsync(
                            session.FailRetryLimit(repairFailure),
                            cancellationToken);
                    }

                    verificationFailures++;
                    var terminalTurn = ScheduleRetryOrFail(session, verificationFailure);
                    if (terminalTurn is not null)
                    {
                        return await TranslateTurnResponseAsync(
                            terminalTurn, cancellationToken);
                    }

                    continue;
                }

                verificationFailures = 0;
                var turn = session.Complete(decision.CompletionMessage);
                await TryLearnFromPostTaskAsync(session, cancellationToken);
                return await TranslateTurnResponseAsync(turn, cancellationToken);
            }

            if (session.StepLimitExceeded)
            {
                return await TranslateTurnResponseAsync(
                    session.FailStepLimit(), cancellationToken);
            }

            var actionResult = await ExecuteActionAsync(
                session,
                decision.Action!,
                cancellationToken);
            if (actionResult.TerminalTurn is not null)
            {
                return await TranslateTurnResponseAsync(
                    actionResult.TerminalTurn, cancellationToken);
            }

            if (actionResult.RequiresRetry)
            {
                var terminalTurn = ScheduleRetryOrFail(session, actionResult.Observation);
                if (terminalTurn is not null)
                {
                    return await TranslateTurnResponseAsync(
                        terminalTurn, cancellationToken);
                }

                continue;
            }

            session.CompleteStep(decision.Action!.Objective, actionResult.Observation);
            await PersistRunAsync(
                session.Request,
                session.Snapshot(ActionExecutionStatus.Observing),
                cancellationToken,
                isFinal: false);
        }
    }

    private async Task TryLearnFromPostTaskAsync(
        AgentActionSession session,
        CancellationToken cancellationToken)
    {
        if (postTaskLearning is null)
        {
            return;
        }

        try
        {
            var snapshot = new PostTaskRunSnapshot(
                string.IsNullOrWhiteSpace(session.TranslatedObjective)
                    ? session.Request.Prompt
                    : session.TranslatedObjective,
                session.ExecutionHistory.Entries
                    .Where(entry => entry.Success)
                    .Select(entry => entry.Command)
                    .Where(command => !string.IsNullOrWhiteSpace(command))
                    .ToList(),
                session.CreatedArtifacts.Keys
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToList());
            await postTaskLearning.TryLearnFromRunAsync(
                snapshot,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.Log(
                $"[AGENT] Post-task learning hook failed (non-fatal): {ex.Message}");
        }
    }

    private async Task TranslateObjectiveToEnglish(
        AgentActionSession session,
        CancellationToken cancellationToken)
    {
        if (translationService is null ||
            string.IsNullOrWhiteSpace(session.Request.Prompt))
        {
            return;
        }

        try
        {
            var translated = await translationService.TranslateAsync(
                session.Request.Prompt,
                "English",
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(translated) &&
                !string.Equals(translated, session.Request.Prompt, StringComparison.OrdinalIgnoreCase))
            {
                session.TranslatedObjective = translated;
                logger.Log(
                    $"[AGENT] Translated objective: " +
                    $"\"{TruncateForLog(session.Request.Prompt)}\" -> " +
                    $"\"{TruncateForLog(translated)}\"");
            }
        }
        catch (Exception ex)
        {
            logger.Log($"[AGENT] Translation failed for objective, using original: {ex.Message}");
        }
    }

    private async Task<string> TranslateToResponseLanguageAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (translationService is null ||
            string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        try
        {
            var targetLanguage = runtimeSettings.ResponseLanguageName;
            if (string.IsNullOrWhiteSpace(targetLanguage) ||
                targetLanguage.Equals("English", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            var translated = await translationService.TranslateAsync(
                text,
                targetLanguage,
                cancellationToken: cancellationToken);

            return string.IsNullOrWhiteSpace(translated) ? text : translated;
        }
        catch (Exception ex)
        {
            logger.Log(
                $"[AGENT] Response translation failed, using original: {ex.Message}");
            return text;
        }
    }

    private async Task<ConversationTurn> TranslateTurnResponseAsync(
        ConversationTurn turn,
        CancellationToken cancellationToken)
    {
        if (translationService is null || string.IsNullOrWhiteSpace(turn.Response))
        {
            return turn;
        }

        var translated = await TranslateToResponseLanguageAsync(
            turn.Response, cancellationToken);

        if (!string.IsNullOrWhiteSpace(translated) &&
            !string.Equals(translated, turn.Response, StringComparison.Ordinal))
        {
            turn.Response = translated;
        }

        return turn;
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
            if (decision.IsComplete && session.Evidence.Count == 0)
            {
                var failure = "You claimed the task is complete, but no actions were executed. " +
                              "You must execute at least one action and collect evidence before completing.";
                session.RecordDecisionFailure(failure, "Premature completion without evidence");
                return DecisionAttempt.Failed(failure);
            }
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
            OperationKind.ProjectScaffold =>
                await ExecuteProjectScaffoldAsync(
                    session,
                    action,
                    step,
                    cancellationToken),
            OperationKind.PlannedPatch =>
                await ExecutePlannedPatchAsync(
                    session,
                    action,
                    step,
                    cancellationToken),
            _ => ActionAttemptResult.Terminal(
                session.BlockUnsupportedOperation(operationKind))
        };
    }

    private async Task<ActionAttemptResult> ExecuteProjectScaffoldAsync(
        AgentActionSession session,
        AgentToolAction action,
        AgentStep step,
        CancellationToken cancellationToken)
    {
        if (projectScaffolder is null || projectTemplateCatalog is null)
        {
            return ActionAttemptResult.Terminal(
                session.BlockUnsupportedOperation(OperationKind.ProjectScaffold));
        }

        var execution = session.CreateExecution(
            action,
            step,
            OperationKind.ProjectScaffold);
        var targetDirectory = ResolveOperationPath(
            action.TargetPath ?? execution.WorkingDirectory,
            execution.WorkingDirectory);
        execution.TargetPath = targetDirectory;
        execution.Run = $"scaffold \"{targetDirectory}\"";
        execution.OriginalCommand = action.Command;

        var template = projectTemplateCatalog.FindById(
            action.TemplateId ?? string.Empty)
            ?? projectTemplateCatalog.Suggest(
                action.Objective,
                null);
        if (template is null)
        {
            execution.Executed = false;
            execution.ExitCode = -1;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.Error =
                "No project template matched the requested objective.";
            execution.Notes = execution.Error;
            session.Commands.Add(execution);
            session.RecordObservation(execution.Run, execution.Notes);
            return ActionAttemptResult.Retry(execution.Notes);
        }

        execution.TemplateId = template.Id;

        var classification = scaffoldClassifier.Classify(targetDirectory);
        var decision = await operationPolicyEngine.EvaluateAsync(
            new OperationPolicyRequest(
                session.Request.ConversationId,
                execution.StepId,
                OperationKind.ProjectScaffold,
                session.Request.Prompt,
                ResolvedCommand: null,
                targetDirectory,
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
            !await TryApplyApprovalOverrideAsync(
                session,
                execution,
                decision,
                cancellationToken))
        {
            return ActionAttemptResult.Terminal(
                session.RequestCommandApproval(execution));
        }

        session.EmitActionStarted(execution);
        try
        {
            var result = await projectScaffolder.ScaffoldAsync(
                new ProjectScaffoldRequest(
                    template.Id,
                    targetDirectory,
                    ProjectName: Path.GetFileName(targetDirectory.TrimEnd('/', '\\'))),
                cancellationToken);

            if (!result.Success)
            {
                execution.Executed = false;
                execution.ExitCode = -1;
                execution.ExecutedAt = DateTimeOffset.UtcNow;
                execution.StandardError = result.Error ?? string.Empty;
                execution.Error = result.Error;
                execution.Notes = $"Scaffold failed: {result.Error}";
                session.RecordEvidence(evidenceCollector.Collect(
                    new ExecutionEvidenceInput(
                        session.Request.ConversationId,
                        execution.StepId,
                        OperationKind.ProjectScaffold,
                        Command: result.TemplateId,
                        FilePath: targetDirectory,
                        Executed: true,
                        ExitCode: -1,
                        StdErr: result.Error,
                        Success: false)));
                session.EmitActionCompleted(execution);
                session.RecordObservation(execution.Run, execution.Notes);
                return ActionAttemptResult.Retry(execution.Notes);
            }

            execution.Executed = true;
            execution.ExitCode = 0;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.StandardOutput =
                $"Project scaffolded from template '{result.TemplateId}'." +
                $"{Environment.NewLine}Files created ({result.CreatedFiles.Count}):" +
                $"{Environment.NewLine}{string.Join(Environment.NewLine, result.CreatedFiles)}";
            execution.Output = execution.StandardOutput;
            execution.Notes =
                "Scaffold completed. Verification commands: " +
                string.Join(" | ", result.VerificationCommands);

            var evidence = evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    OperationKind.ProjectScaffold,
                    Command: result.TemplateId,
                    FilePath: targetDirectory,
                    Content: string.Join(Environment.NewLine, result.CreatedFiles),
                    Executed: true,
                    ExitCode: 0,
                    StdOut: execution.StandardOutput,
                    Success: true));
            session.RecordEvidence(evidence);
            execution.ContentHash = evidence.ContentHash;

            foreach (var createdFile in result.CreatedFiles)
            {
                session.RecordArtifact(
                    Path.Combine(targetDirectory, createdFile),
                    evidence.ContentHash ?? string.Empty,
                    classification);
            }

            session.EmitActionCompleted(execution);
            var observation =
                $"Template: {result.TemplateId}{Environment.NewLine}" +
                $"Files created: {result.CreatedFiles.Count}{Environment.NewLine}" +
                $"Verification commands: {string.Join(" | ", result.VerificationCommands)}";
            session.EmitToolObservation(
                execution,
                observation,
                execution.StandardOutput);
            session.RecordObservation(execution.Run, observation);

            var validation = projectStackValidator is null
                ? null
                : await projectStackValidator.ValidateAsync(
                    targetDirectory,
                    template.Id,
                    cancellationToken);
            if (validation is not null && !validation.Success)
            {
                return ActionAttemptResult.Completed(
                    $"{observation}{Environment.NewLine}" +
                    $"Stack validation found missing files: " +
                    $"{string.Join(", ", validation.MissingEssentialFiles)}");
            }

            return ActionAttemptResult.Completed(observation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            execution.Executed = false;
            execution.ExitCode = -1;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.StandardError = ex.Message;
            execution.Error = ex.Message;
            execution.Notes = $"Scaffold failed: {ex.Message}";
            session.RecordEvidence(evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    OperationKind.ProjectScaffold,
                    Command: template.Id,
                    FilePath: targetDirectory,
                    Executed: true,
                    ExitCode: -1,
                    StdErr: ex.Message,
                    Success: false)));
            session.EmitActionCompleted(execution);
            session.RecordObservation(execution.Run, execution.Notes);
            return ActionAttemptResult.Retry(execution.Notes);
        }
    }

    private async Task<ActionAttemptResult> ExecutePlannedPatchAsync(
        AgentActionSession session,
        AgentToolAction action,
        AgentStep step,
        CancellationToken cancellationToken)
    {
        var files = action.PlannedFiles ?? [];
        if (files.Count == 0 || plannedPatchApplier is null)
        {
            return ActionAttemptResult.Terminal(
                session.BlockUnsupportedOperation(OperationKind.PlannedPatch));
        }

        var execution = session.CreateExecution(
            action,
            step,
            OperationKind.PlannedPatch);
        var targetDirectory = ResolveOperationPath(
            action.TargetPath ?? execution.WorkingDirectory,
            execution.WorkingDirectory);
        execution.TargetPath = targetDirectory;
        execution.PlannedFiles = files;
        execution.Run = $"apply-patch ({files.Count} files)";
        execution.OriginalCommand = action.Command;
        execution.Notes = BuildPatchSummary(targetDirectory, files);
        session.Commands.Add(execution);

        var classification = ClassifyPlannedPatch(
            session,
            targetDirectory,
            files);
        var decision = await operationPolicyEngine.EvaluateAsync(
            new OperationPolicyRequest(
                session.Request.ConversationId,
                execution.StepId,
                OperationKind.PlannedPatch,
                session.Request.Prompt,
                ResolvedCommand: null,
                targetDirectory,
                classification),
            cancellationToken);
        ApplyOperationClassification(execution, classification, decision);
        execution.Notes = $"{BuildPatchSummary(targetDirectory, files)} {execution.Notes}";

        if (decision.Decision == CommandSafetyDecisionType.Block)
        {
            return ActionAttemptResult.Terminal(
                session.BlockUnsafeCommand(execution));
        }

        if (decision.Decision == CommandSafetyDecisionType.AskApproval &&
            !CanApproveCreationInSandbox(execution, classification) &&
            !await TryApplyApprovalOverrideAsync(
                session,
                execution,
                decision,
                cancellationToken))
        {
            return ActionAttemptResult.Terminal(
                session.RequestCommandApproval(execution));
        }

        if (decision.Decision == CommandSafetyDecisionType.AskApproval &&
            CanApproveCreationInSandbox(execution, classification))
        {
            execution.Notes = AppendApprovalNote(
                execution.Notes,
                "Patch permitido automaticamente: o sandbox Docker isola a execucao e os arquivos estao dentro do workspace.");
        }

        session.EmitActionStarted(execution);
        try
        {
            var result = await plannedPatchApplier.ApplyAsync(
                new PlannedPatchRequest(
                    action.Objective,
                    files,
                    targetDirectory),
                cancellationToken);

            if (!result.Success)
            {
                execution.Executed = false;
                execution.ExitCode = -1;
                execution.ExecutedAt = DateTimeOffset.UtcNow;
                execution.StandardError = result.Error ?? string.Empty;
                execution.Error = result.Error;
                execution.Notes = $"Patch failed: {result.Error}";
                session.RecordEvidence(evidenceCollector.Collect(
                    new ExecutionEvidenceInput(
                        session.Request.ConversationId,
                        execution.StepId,
                        OperationKind.PlannedPatch,
                        FilePath: targetDirectory,
                        Executed: true,
                        ExitCode: -1,
                        StdErr: result.Error,
                        Success: false)));
                session.EmitActionCompleted(execution);
                session.RecordObservation(execution.Run, execution.Notes);
                return ActionAttemptResult.Retry(execution.Notes);
            }

            execution.Executed = true;
            execution.ExitCode = 0;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.StandardOutput =
                $"Planned patch applied ({result.AppliedFiles.Count} files):" +
                $"{Environment.NewLine}{string.Join(Environment.NewLine, result.AppliedFiles)}";
            execution.Output = execution.StandardOutput;
            execution.Notes = "Planned patch applied successfully.";

            var evidence = evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    OperationKind.PlannedPatch,
                    Command: action.Objective,
                    FilePath: targetDirectory,
                    Content: string.Join(Environment.NewLine, result.AppliedFiles),
                    Executed: true,
                    ExitCode: 0,
                    StdOut: execution.StandardOutput,
                    Success: true));
            session.RecordEvidence(evidence);
            execution.ContentHash = evidence.ContentHash;

            foreach (var appliedFile in result.AppliedFiles)
            {
                session.RecordArtifact(
                    Path.Combine(targetDirectory, appliedFile),
                    evidence.ContentHash ?? string.Empty,
                    classification);
            }

            session.EmitActionCompleted(execution);
            var observation =
                $"Planned patch applied.{Environment.NewLine}" +
                $"Files: {result.AppliedFiles.Count}{Environment.NewLine}" +
                $"{string.Join(Environment.NewLine, result.AppliedFiles)}";
            session.EmitToolObservation(
                execution,
                observation,
                execution.StandardOutput);
            session.RecordObservation(execution.Run, observation);
            return ActionAttemptResult.Completed(observation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            execution.Executed = false;
            execution.ExitCode = -1;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.StandardError = ex.Message;
            execution.Error = ex.Message;
            execution.Notes = $"Patch failed: {ex.Message}";
            session.RecordEvidence(evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    OperationKind.PlannedPatch,
                    FilePath: targetDirectory,
                    Executed: true,
                    ExitCode: -1,
                    StdErr: ex.Message,
                    Success: false)));
            session.EmitActionCompleted(execution);
            session.RecordObservation(execution.Run, execution.Notes);
            return ActionAttemptResult.Retry(execution.Notes);
        }
    }

    private static string BuildPatchSummary(
        string targetDirectory,
        IReadOnlyList<PlannedPatchFile> files)
    {
        var lines = string.Join(
            Environment.NewLine,
            files.Select(file => $"- {file.RelativePath}"));
        return $"Planned multi-file patch ({files.Count} files) targeting " +
               $"{targetDirectory}:{Environment.NewLine}{lines}";
    }

    private CommandClassification ClassifyPlannedPatch(
        AgentActionSession session,
        string targetDirectory,
        IReadOnlyList<PlannedPatchFile> files)
    {
        var blocking = files
            .Where(file => !IsPathUnder(
                ResolveOperationPath(file.RelativePath, targetDirectory),
                targetDirectory))
            .Select(file => file.RelativePath)
            .ToList();
        if (blocking.Count > 0)
        {
            return NewPatchClassification(
                CommandIntent.Blocked,
                [
                    $"Patch file(s) escape the target directory: " +
                    $"{string.Join(", ", blocking)}"
                ]);
        }

        var classifications = files
            .Select(file =>
            {
                var path = ResolveOperationPath(file.RelativePath, targetDirectory);
                var concurrentModification = TryDetectConcurrentModification(
                    session,
                    path);
                return concurrentModification ??
                       fileWriteSafetyClassifier.Classify(path);
            })
            .ToList();

        if (classifications.Any(classification =>
                classification.Intent is
                    CommandIntent.Blocked or
                    CommandIntent.DataExfiltration))
        {
            var reasons = classifications
                .Where(classification =>
                    classification.Intent is
                        CommandIntent.Blocked or
                        CommandIntent.DataExfiltration)
                .SelectMany(classification => classification.Reasons)
                .ToList();
            return NewPatchClassification(CommandIntent.Blocked, reasons);
        }

        if (classifications.Any(classification =>
                classification.Intent == CommandIntent.NeedsApproval))
        {
            var reasons = classifications
                .Where(classification =>
                    classification.Intent == CommandIntent.NeedsApproval)
                .SelectMany(classification => classification.Reasons)
                .ToList();
            return NewPatchClassification(CommandIntent.NeedsApproval, reasons);
        }

        return NewPatchClassification(
            CommandIntent.SafeWriteLocal,
            ["All planned patch files are safe local writes inside the allowed roots."]);
    }

    private static CommandClassification NewPatchClassification(
        CommandIntent intent,
        IReadOnlyList<string> reasons) =>
        new(
            nameof(OperationKind.PlannedPatch),
            intent,
            0.99,
            "PlannedPatchSafetyClassifier",
            reasons);

    private static bool IsPathUnder(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
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
                var sandboxWillIsolate =
                    artifactDecision.Decision == CommandSafetyDecisionType.AskApproval &&
                    CanRunInSandbox(execution) &&
                    CanApproveCreationInSandbox(execution);
                var allowlistApplied =
                    artifactDecision.Decision == CommandSafetyDecisionType.AskApproval &&
                    await TryApplyWorkspaceAllowlist(
                        session,
                        execution,
                        artifactDecision,
                        cancellationToken);
                if (!allowlistApplied &&
                    !sandboxWillIsolate &&
                    !await TryApplyApprovalOverrideAsync(
                        session,
                        execution,
                        artifactDecision,
                        cancellationToken))
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
            await TryApplyApprovalOverrideAsync(
                session,
                execution,
                validation.SafetyDecision,
                cancellationToken);
        var workspaceAllowlistApplied =
            !approvalOverrideApplied &&
            await TryApplyWorkspaceAllowlist(
                session,
                execution,
                validation.SafetyDecision,
                cancellationToken);

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
            !approvalOverrideApplied &&
            !workspaceAllowlistApplied)
        {
            if (CanRunInSandbox(execution))
            {
                return await ExecuteSandboxedCommandAsync(
                    session,
                    execution,
                    resolvedCommand,
                    storedCommand,
                    operationKind,
                    environmentSnapshot,
                    cancellationToken);
            }

            return ActionAttemptResult.Terminal(session.RequestCommandApproval(execution));
        }

        if (!validation.Correct)
        {
            var observation = execution.Notes
                ?? "The proposed action does not satisfy the current step.";
            session.RecordCommandObservation(execution.Run, observation);
            return ActionAttemptResult.Retry(observation);
        }

        if (execution.TargetPath is not null &&
            !ValidateTargetPath(execution.TargetPath, out var pathIssue))
        {
            var pathObservation = pathIssue;
            logger.Log($"[AGENT] Invalid target path '{execution.TargetPath}': {pathIssue}");
            session.RecordCommandObservation(execution.Run, pathObservation);
            return ActionAttemptResult.Retry(pathObservation);
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
        var toolCancellationToken = CreateToolCancellationToken(
            execution,
            cancellationToken);
        ShellCommandResult toolResult;
        try
        {
            toolResult = await ExecuteToolAsync(
                execution,
                resolvedCommand,
                CreateStreamObserver(session),
                toolCancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutMessage =
                "The command did not finish within the allowed timeout and was stopped.";
            execution.Executed = false;
            execution.ExitCode = -1;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.StandardError = timeoutMessage;
            execution.Error = timeoutMessage;
            execution.Notes = timeoutMessage;
            session.EmitActionCompleted(execution);
            session.RecordEvidence(evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    operationKind,
                    execution.Run,
                    execution.TargetPath,
                    Executed: true,
                    ExitCode: -1,
                    StdErr: timeoutMessage,
                    Success: false)));
            var timeoutObservation = BuildObservationMessage(execution);
            session.EmitToolObservation(execution, timeoutObservation, timeoutMessage);
            session.RecordObservation(execution.Run, timeoutObservation);
return ActionAttemptResult.Retry(timeoutObservation);
        }

var (observationMessage, historyEntry) = await RecordToolOutcomeAsync(
            session,
            execution,
            toolResult,
            storedCommand,
            operationKind,
            environmentSnapshot,
            cancellationToken);

        if (execution.Executed)
        {
            var output = toolResult.CombinedOutput;
            if (outputVerificationService is not null &&
                !string.IsNullOrWhiteSpace(output))
            {
                try
                {
                    var verifyResult = await outputVerificationService.VerifyAsync(
                        session.Request.Prompt,
                        execution.Run,
                        output,
                        execution.WorkingDirectory,
                        cancellationToken);
                    if (verifyResult.Verdict == OutputVerdict.Mismatch)
                    {
                        logger.Log(
                            $"[AGENT] Output mismatch: {verifyResult.Reason}");
                        observationMessage =
                            $"[Verification: {verifyResult.Reason}]\n{observationMessage}";

                        if (!string.IsNullOrWhiteSpace(verifyResult.CorrectedCommand) &&
                            !verifyResult.CorrectedCommand.Equals(
                                execution.Run, StringComparison.OrdinalIgnoreCase))
                        {
                            logger.Log(
                                $"[AGENT] Auto-correcting with: {verifyResult.CorrectedCommand}");
                            return ActionAttemptResult.Retry(
                                $"Output mismatch: {verifyResult.Reason}. " +
                                $"Retry with corrected command: {verifyResult.CorrectedCommand}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Log(
                        $"[AGENT] Output verification failed: {ex.Message}");
                }
            }

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
            historyEntry.ErrorSignature ?? "unknown");
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
        execution.Content = content;
        execution.Language = action.Language;

        var classification = operationKind == OperationKind.ScriptContent
            ? scriptContentClassifier.Classify(
                content,
                action.Language ?? string.Empty,
                targetPath)
            : fileWriteSafetyClassifier.Classify(targetPath);
        var concurrentModification = TryDetectConcurrentModification(
            session,
            targetPath);
        if (concurrentModification is not null)
        {
            classification = concurrentModification;
        }

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
            !CanApproveCreationInSandbox(execution, classification) &&
            !await TryApplyApprovalOverrideAsync(
                session,
                execution,
                decision,
                cancellationToken))
        {
            return ActionAttemptResult.Terminal(
                session.RequestCommandApproval(execution));
        }

        if (decision.Decision == CommandSafetyDecisionType.AskApproval &&
            CanApproveCreationInSandbox(execution, classification))
        {
            execution.Notes = AppendApprovalNote(
                execution.Notes,
                "Criacao permitida automaticamente: o sandbox Docker isola a execucao e o alvo esta dentro do workspace.");
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
            if (operationKind is OperationKind.ScriptContent or OperationKind.FileWrite)
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

            if (learningFromExecution is not null && execution.ContentHash is not null)
            {
                try
                {
                    await learningFromExecution.RecordSuccessfulFileOperationAsync(
                        operationKind.ToString(),
                        targetPath,
                        execution.ContentHash,
                        session.Request.ConversationId,
                        execution.StepId,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.Log($"[AGENT] Learning from file operation failed (non-fatal): {ex.Message}");
                }
            }

            var verifyResult = await AutoVerifyFileSystemAsync(
                targetPath,
                execution,
                session,
                cancellationToken);
            var finalObservation = string.IsNullOrWhiteSpace(verifyResult)
                ? observation
                : $"{observation}{Environment.NewLine}{verifyResult}";

            return ActionAttemptResult.Completed(finalObservation);
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
            !await TryApplyApprovalOverrideAsync(
                session,
                execution,
                decision,
                cancellationToken))
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

    private CancellationToken CreateToolCancellationToken(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = execution.OperationKind == OperationKind.ScriptExecution
            ? runtimeSettings.ScriptTimeoutSeconds
            : runtimeSettings.CommandTimeoutSeconds;
        if (timeoutSeconds <= 0)
        {
            return cancellationToken;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return source.Token;
    }

    private static CommandClassification? TryDetectConcurrentModification(
        AgentActionSession session,
        string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(targetPath);
            if (session.CreatedArtifacts.ContainsKey(fullPath) ||
                !File.Exists(fullPath))
            {
                return null;
            }

            if (File.GetLastWriteTimeUtc(fullPath) <= session.RunStartedUtc)
            {
                return null;
            }

            return new CommandClassification(
                fullPath,
                CommandIntent.NeedsApproval,
                0.99,
                "ConcurrentModificationGuard",
                [
                    "The target file was modified after the agent run started. " +
                    "Overwriting it requires explicit approval."
                ]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool CanRunInSandbox(CommandExecution execution) =>
        commandSandbox is not null &&
        commandSandbox.Mode != SandboxMode.Disabled &&
        commandSandbox.IsEligible(execution.Shell) &&
        execution.OperationKind is
            OperationKind.TerminalCommand or
            OperationKind.ScriptExecution;

    private bool CanApproveCreationInSandbox(
        CommandExecution execution,
        CommandClassification? classification = null) =>
        commandSandbox is not null &&
        commandSandbox.Mode != SandboxMode.Disabled &&
        !string.IsNullOrWhiteSpace(execution.TargetPath) &&
        IsPathUnder(execution.TargetPath, execution.WorkingDirectory) &&
        !IsConcurrentModification(classification);

    private static bool IsConcurrentModification(
        CommandClassification? classification)
    {
        if (classification is null)
        {
            return false;
        }

        if (classification.Source == "ConcurrentModificationGuard")
        {
            return true;
        }

        return classification.Reasons.Any(reason =>
            reason.Contains(
                "modified after the agent run started",
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ActionAttemptResult> ExecuteSandboxedCommandAsync(
        AgentActionSession session,
        CommandExecution execution,
        ResolvedCommand resolvedCommand,
        StoredCommand? storedCommand,
        OperationKind operationKind,
        ExecutionEnvironmentSnapshot environmentSnapshot,
        CancellationToken cancellationToken)
    {
        session.EmitActionStarted(execution);
        var toolCancellationToken = CreateToolCancellationToken(
            execution,
            cancellationToken);
        ShellCommandResult toolResult;
        try
        {
            toolResult = await commandSandbox!.RunSandboxedAsync(
                execution.Shell,
                resolvedCommand,
                execution.WorkingDirectory,
                toolCancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutMessage =
                "The sandboxed command did not finish within the allowed timeout and was stopped.";
            execution.Executed = false;
            execution.ExitCode = -1;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.StandardError = timeoutMessage;
            execution.Error = timeoutMessage;
            execution.Notes = timeoutMessage;
            session.EmitActionCompleted(execution);
            session.RecordEvidence(evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    operationKind,
                    execution.Run,
                    execution.TargetPath,
                    Executed: true,
                    ExitCode: -1,
                    StdErr: timeoutMessage,
                    Success: false)));
            var timeoutObservation = BuildObservationMessage(execution);
            session.EmitToolObservation(execution, timeoutObservation, timeoutMessage);
            session.RecordObservation(execution.Run, timeoutObservation);
            return ActionAttemptResult.Retry(timeoutObservation);
        }
        catch (Exception ex)
        {
            var sandboxFailure =
                $"O sandbox de comandos falhou ({ex.Message}). " +
                "O comando nao foi executado; reformule ou solicite aprovacao manual.";
            execution.Executed = false;
            execution.ExitCode = -1;
            execution.ExecutedAt = DateTimeOffset.UtcNow;
            execution.StandardError = sandboxFailure;
            execution.Error = sandboxFailure;
            execution.Notes = sandboxFailure;
            session.EmitActionCompleted(execution);
            session.RecordEvidence(evidenceCollector.Collect(
                new ExecutionEvidenceInput(
                    session.Request.ConversationId,
                    execution.StepId,
                    operationKind,
                    execution.Run,
                    execution.TargetPath,
                    Executed: true,
                    ExitCode: -1,
                    StdErr: sandboxFailure,
                    Success: false)));
            session.RecordObservation(execution.Run, sandboxFailure);
            return ActionAttemptResult.Retry(sandboxFailure);
        }

        ApplyToolResult(execution, toolResult);
        execution.Sandboxed = true;
        execution.Notes = AppendApprovalNote(
            execution.Notes,
            "Executado no sandbox Docker (sem rede, sem privilegios).");

        var (observationMessage, _) = await RecordToolOutcomeAsync(
            session,
            execution,
            toolResult,
            storedCommand,
            operationKind,
            environmentSnapshot,
            cancellationToken);

        if (execution.Executed)
        {
            return ActionAttemptResult.Completed(observationMessage);
        }

        return ActionAttemptResult.Retry(observationMessage);
    }

    private async Task<(string Observation, ExecutionHistoryEntry HistoryEntry)> RecordToolOutcomeAsync(
        AgentActionSession session,
        CommandExecution execution,
        ShellCommandResult toolResult,
        StoredCommand? storedCommand,
        OperationKind operationKind,
        ExecutionEnvironmentSnapshot environmentSnapshot,
        CancellationToken cancellationToken)
    {
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

        await commandAuditService.UpdateExecutionDetailsAsync(
            storedCommand?.Id,
            execution,
            execution.Executed ? toolResult.CombinedOutput : observationMessage,
            cancellationToken);

        if (learningFromExecution is not null)
        {
            try
            {
                var errorCategory = execution.Executed
                    ? "Success"
                    : historyEntry.ErrorSignature ?? "Other";
                if (execution.Executed)
                {
                    await learningFromExecution.RecordSuccessfulCommandAsync(
                        execution.Run,
                        execution.ResolvedFileName ?? execution.Run,
                        execution.WorkingDirectory,
                        toolResult.ExitCode,
                        toolResult.StandardOutput ?? string.Empty,
                        toolResult.StandardError ?? string.Empty,
                        session.Request.ConversationId,
                        execution.StepId,
                        cancellationToken);
                }
                else
                {
                    await learningFromExecution.RecordFailedCommandAsync(
                        execution.Run,
                        execution.ResolvedFileName ?? execution.Run,
                        execution.WorkingDirectory,
                        toolResult.ExitCode,
                        toolResult.StandardOutput ?? string.Empty,
                        toolResult.StandardError ?? string.Empty,
                        errorCategory,
                        session.Request.ConversationId,
                        execution.StepId,
                        cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.Log($"[AGENT] Learning from execution failed (non-fatal): {ex.Message}");
            }
        }

        if (workspaceMemoryService is not null && execution.ExitCode is 0)
        {
            try
            {
                await workspaceMemoryService.RecordSuccessfulCommandAsync(
                    execution.WorkingDirectory,
                    execution,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.Log($"[AGENT] Workspace memory failed (non-fatal): {ex.Message}");
            }
        }

return (observationMessage, historyEntry);
    }

    private static IShellOutputObserver? CreateStreamObserver(AgentActionSession session)
    {
        return new DelegateShellOutputObserver((chunk, isError) =>
        {
            session.EmitStreamOutput(chunk, isError);
        });
    }

    private sealed class DelegateShellOutputObserver : IShellOutputObserver
    {
        private readonly Action<string, bool> _onOutput;

        public DelegateShellOutputObserver(Action<string, bool> onOutput)
        {
            _onOutput = onOutput;
        }

        public void OnOutput(string chunk, bool isError)
        {
            _onOutput(chunk, isError);
        }
    }

    private async Task<ShellCommandResult> ExecuteToolAsync(
        CommandExecution execution,
        ResolvedCommand resolvedCommand,
        IShellOutputObserver? streamObserver,
        CancellationToken cancellationToken)
    {
        try
        {
            ShellCommandResult result;
            if (streamObserver is not null && executor is IStreamingShellExecutor streamingExecutor)
            {
                result = await streamingExecutor.RunCommandDetailedAsync(
                    resolvedCommand,
                    streamObserver,
                    cancellationToken);
            }
            else if (executor is IResolvedCommandExecutor resolvedExecutor)
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
            execution.Notes = AppendApprovalNote(
                execution.Notes,
                $"Falha ao executar o comando (exit code {result.ExitCode}). " +
                $"{execution.Error}");
            return;
        }

        execution.Notes = AppendApprovalNote(
            execution.Notes,
            string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? "Comando executado sem saida textual."
                : "Comando executado com sucesso.");
    }

    private static ConversationTurn? ScheduleRetryOrFail(
        AgentActionSession session,
        string failure)
    {
        return session.TryScheduleRetry(failure)
            ? null
            : session.FailRetryLimit(failure);
    }

    private async Task<string?> VerifyCompletionDeterministicallyAsync(
        AgentActionSession session,
        CancellationToken cancellationToken)
    {
        if (deterministicVerification is null)
        {
            return null;
        }

        if (!runtimeSettings.RequireDeterministicVerification)
        {
            logger.Log(
                "[AGENT] Deterministic verification skipped because " +
                "RequireDeterministicVerification is disabled.");
            return null;
        }

        var workingDirectory = ResolveSessionWorkingDirectory(session);
        try
        {
            var result = await deterministicVerification.VerifyAsync(
                workingDirectory,
                session.Evidence,
                cancellationToken);
            if (result.Verdict == DeterministicVerificationVerdict.Failed)
            {
                logger.Log(
                    $"[AGENT] Deterministic verification failed before completion: " +
                    $"tool={result.Tool}; command={result.Command}; " +
                    $"exitCode={result.ExitCode}");
                return
                    $"A verificacao deterministica falhou antes de aceitar a conclusao. " +
                    $"Ferramenta: {result.Tool}. " +
                    $"Comando: {result.Command} (exit code {result.ExitCode}). " +
                    TruncateForLog(result.Output ?? string.Empty, 1200);
            }

            if (result.Verdict == DeterministicVerificationVerdict.Passed)
            {
                logger.Log(
                    $"[AGENT] Deterministic verification passed: " +
                    $"tool={result.Tool}; command={result.Command}");
            }
            else if (result.Verdict == DeterministicVerificationVerdict.NotApplicable)
            {
                logger.Log(
                    $"[AGENT] Deterministic verification not applicable: " +
                    $"{result.Output}");
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[AGENT] Deterministic verification step failed (non-fatal): {ex.Message}");
            return null;
        }
    }

    private static string ResolveSessionWorkingDirectory(AgentActionSession session)
    {
        var lastFilePath = session.Evidence
            .Select(evidence => evidence.FilePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (lastFilePath is not null)
        {
            try
            {
                return Path.GetDirectoryName(lastFilePath) ?? session.WorkspaceRoot.Root;
            }
            catch (Exception)
            {
                // fall through to the caller's default
            }
        }

        return session.Request.ApprovedAction?.WorkingDirectory
            ?? session.WorkspaceRoot.Root;
    }

    private async Task EnrichTurnWithGitDiffAsync(
        ConversationTurn turn,
        AgentActionSession session,
        CancellationToken cancellationToken)
    {
        if (gitDiffService is null)
        {
            return;
        }

        try
        {
            var workingDirectory = ResolveSessionWorkingDirectory(session);
            var diff = await gitDiffService.GetWorkingTreeDiffAsync(
                workingDirectory,
                cancellationToken);
            if (!diff.IsRepository)
            {
                return;
            }

            var section = new System.Text.StringBuilder();
            section.AppendLine();
            section.AppendLine("## Diff do working tree");
            section.AppendLine(string.IsNullOrWhiteSpace(diff.DiffStat)
                ? "Nenhuma alteracao detectada no working tree."
                : diff.DiffStat);
            section.AppendLine();
            section.AppendLine("## Arquivos alterados no working tree");
            section.AppendLine(diff.ChangedFiles.Count == 0
                ? "Nenhum arquivo alterado."
                : string.Join(Environment.NewLine, diff.ChangedFiles.Select(file => $"- {file}")));

            var unrelated = FindUnrelatedChanges(diff.ChangedFiles, session);
            if (unrelated.Count > 0)
            {
                section.AppendLine();
                section.AppendLine("## Aviso: alteracoes fora da acao do agente");
                section.AppendLine(
                    "Estes arquivos mudaram no working tree mas nao foram tocados " +
                    "por esta execucao (possivel alteracao manual durante o run):");
                section.AppendLine(string.Join(Environment.NewLine, unrelated.Select(file => $"- {file}")));
            }

            turn.FinalReport = string.IsNullOrWhiteSpace(turn.FinalReport)
                ? section.ToString().Trim()
                : turn.FinalReport.TrimEnd() + section.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[AGENT] Git diff enrichment failed (non-fatal): {ex.Message}");
        }
    }

    private static List<string> FindUnrelatedChanges(
        IReadOnlyList<string> changedFiles,
        AgentActionSession session)
    {
        var touchedByAgent = session.Evidence
            .Where(evidence => evidence.Success)
            .Select(evidence => evidence.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unrelated = new List<string>();
        foreach (var changedFile in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(changedFile))
            {
                continue;
            }

            var normalized = changedFile.Replace('/', Path.DirectorySeparatorChar);
            var isTouched = touchedByAgent.Any(touched =>
                string.Equals(
                    Path.GetFileName(touched),
                    Path.GetFileName(normalized),
                    StringComparison.OrdinalIgnoreCase));
            if (!isTouched)
            {
                unrelated.Add(changedFile);
            }
        }

        return unrelated;
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
        if (inside)
        {
            return new CommandClassification(
                targetPath,
                CommandIntent.SafeReadOnly,
                0.99,
                "FileReadSafetyClassifier",
                ["The non-sensitive file is inside the active workspace."]);
        }

        if (IsOperatingSystemRoot(targetPath))
        {
            return new CommandClassification(
                targetPath,
                CommandIntent.NeedsApproval,
                0.99,
                "FileReadSafetyClassifier",
                ["Reading a file under operating system roots requires approval."]);
        }

        return new CommandClassification(
            targetPath,
            CommandIntent.SafeReadOnly,
            0.98,
            "FileReadSafetyClassifier",
            ["The non-sensitive file is outside the workspace but read-only access is allowed."]);
    }

    private static bool IsOperatingSystemRoot(string path)
    {
        string[] systemRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")
        ];

        var fullPath = Path.GetFullPath(path);
        foreach (var systemRoot in systemRoots)
        {
            if (string.IsNullOrWhiteSpace(systemRoot))
            {
                continue;
            }

            var relative = Path.GetRelativePath(
                Path.GetFullPath(systemRoot),
                fullPath);
            var underSystemRoot = relative != ".." &&
                !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative);
            if (underSystemRoot)
            {
                return true;
            }
        }

        return false;
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

    private async Task<bool> TryApplyApprovalOverrideAsync(
        AgentActionSession session,
        CommandExecution execution,
        CommandSafetyDecision decision,
        CancellationToken cancellationToken)
    {
        if (!approvalService.IsApprovalOverridable(execution.OperationKind))
        {
            return false;
        }

        var approvedAction = session.Request.ApprovedAction;
        var approvedByUser = approvedAction is not null;

        IReadOnlyList<WorkspaceMemoryEntry> workspaceCategoryEntries = [];
        if (workspaceCategoryPolicyService is not null)
        {
            workspaceCategoryEntries =
                await workspaceCategoryPolicyService.ListAsync(
                    session.WorkspaceRoot.Root,
                    cancellationToken);
        }

        var result = approvalService.EvaluateOverride(
            decision,
            execution.OperationKind,
            new ApprovalOverrideInput(
                approvedByUser,
                session.ApprovedCommandsForConversation.Contains(
                    CommandNormalization.Normalize(execution.Run)),
                runtimeSettings.AutoApproveCommands,
                runtimeSettings.AutoApproveCategories,
                workspaceCategoryEntries
                    .Select(entry => entry.Value)
                    .ToList()));
        if (!result.CanProceed)
        {
            return false;
        }

        if (approvedAction?.Scope == ApprovalScope.Workspace)
        {
            if (commandAllowlistService is not null)
            {
                await commandAllowlistService.AddAsync(
                    session.WorkspaceRoot.Root,
                    execution.Run,
                    evidence: "Aprovado manualmente para este workspace.",
                    cancellationToken);
            }

            result = new ApprovalOverrideResult(
                ApprovalOverrideSource.Workspace,
                "Aprovado manualmente e salvo na allowlist deste workspace.");
        }
        else if (approvedAction?.Scope == ApprovalScope.Category)
        {
            var category = CommandApprovalService.CategorizeIntent(decision.Intent);
            if (workspaceCategoryPolicyService is not null)
            {
                await workspaceCategoryPolicyService.AddAsync(
                    session.WorkspaceRoot.Root,
                    category,
                    evidence: "Aprovado manualmente para este workspace.",
                    cancellationToken);
            }

            result = new ApprovalOverrideResult(
                ApprovalOverrideSource.Category,
                $"Aprovado manualmente; a categoria '{category}' agora e auto-aprovada neste workspace.");
        }

        execution.ApprovedByUser = result.Source is
            ApprovalOverrideSource.Manual or
            ApprovalOverrideSource.Conversation or
            ApprovalOverrideSource.Workspace;
        execution.AutoApproved = result.Source is
            ApprovalOverrideSource.Auto or
            ApprovalOverrideSource.Category;
        execution.SafetyDecision = decision.Decision;
        execution.Notes = AppendApprovalNote(execution.Notes, result.Note);
        session.RecordApproval(execution, execution.AutoApproved);
        session.EmitApprovalGranted(execution);
        return true;
    }

    private async Task<bool> TryApplyWorkspaceAllowlist(
        AgentActionSession session,
        CommandExecution execution,
        CommandSafetyDecision decision,
        CancellationToken cancellationToken)
    {
        if (commandAllowlistService is null ||
            decision.Decision != CommandSafetyDecisionType.AskApproval ||
            execution.OperationKind is not (
                OperationKind.TerminalCommand or
                OperationKind.ScriptExecution) ||
            string.IsNullOrWhiteSpace(execution.Run))
        {
            return false;
        }

        var allowed = await commandAllowlistService.IsAllowedAsync(
            session.WorkspaceRoot.Root,
            execution.Run,
            cancellationToken);
        if (!allowed)
        {
            return false;
        }

        execution.AutoApproved = true;
        execution.SafetyDecision = decision.Decision;
        execution.Notes = AppendApprovalNote(
            execution.Notes,
            "Aprovado pela allowlist deste workspace.");
        session.RecordApproval(execution, autoApproved: true);
        session.EmitApprovalGranted(execution);
        return true;
    }

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

    private async Task<string> QueryRelevantKnowledgeAsync(
        string objective,
        CancellationToken cancellationToken)
    {
        try
        {
            var answer = await knowledgeQueryService.AnswerForAutomationAsync(
                objective,
                cancellationToken);
            if (answer is not null &&
                !answer.Contains("Nao ha conhecimento", StringComparison.OrdinalIgnoreCase) &&
                !answer.Contains("Não há conhecimento", StringComparison.OrdinalIgnoreCase))
            {
                logger.Log(
                    $"[AGENT] Injected {answer.Length} chars of relevant knowledge for: {objective}");
                return answer;
            }
        }
        catch (Exception ex)
        {
            logger.Log(
                $"[AGENT] Knowledge query failed: {ex.Message}");
        }

        return string.Empty;
    }

    private static string SanitizeJson(string json)
    {
        var sb = new StringBuilder(json.Length);
        for (var i = 0; i < json.Length; i++)
        {
            if (json[i] == '\\' && i + 1 < json.Length)
            {
                var next = json[i + 1];
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u')
                {
                    sb.Append('\\');
                    sb.Append(next);
                    i++;
                }
                else
                {
                    sb.Append("\\\\");
                    sb.Append(next);
                    i++;
                }
            }
            else
            {
                sb.Append(json[i]);
            }
        }
        return sb.ToString();
    }

    private async Task PersistRunAsync(
        AgentActionRunRequest request,
        ConversationTurn turn,
        CancellationToken cancellationToken,
        bool isFinal = true)
    {
        if (agentRunStore is null)
        {
            return;
        }

        try
        {
            var status = turn.ActionStatus?.ToString() ?? "Unknown";
            var run = new AgentRun(
                turn.RequestId,
                turn.ConversationId,
                turn.RequestId,
                turn.Prompt,
                turn.ModelName,
                status,
                turn.ActionEvents.Count > 0
                    ? turn.ActionEvents[0].CreatedAt
                    : DateTimeOffset.UtcNow,
                isFinal ? DateTimeOffset.UtcNow : null,
                turn.Response,
                turn.IsCancelled,
                BuildStepRecords(turn),
                string.IsNullOrWhiteSpace(turn.CurrentPlan) ? null : turn.CurrentPlan,
                turn.Artifacts,
                turn.Approvals,
                ReferenceWorkspace.Resolve(request.WorkspaceRoot).Root);
            await agentRunStore.SaveRunAsync(run, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[AGENT] Failed to persist agent run: {ex.Message}");
        }
    }

    private static IReadOnlyList<AgentStepRecord> BuildStepRecords(ConversationTurn turn)
    {
        var records = new List<AgentStepRecord>();
        foreach (var command in turn.Commands)
        {
            records.Add(new AgentStepRecord(
                command.StepId,
                turn.RequestId,
                command.Id,
                command.Attempt,
                command.OperationKind,
                command.Objective,
                command.Run,
                command.WorkingDirectory,
                command.TargetPath,
                command.ExitCode,
                command.ExitCode == 0 || (command.ExitCode is null && command.Executed),
                command.ExecutedAt ?? DateTimeOffset.UtcNow,
                command.StandardOutput,
                command.StandardError,
                command.Shell.ToString(),
                command.SafetyDecision,
                command.ApprovedByUser,
                command.AutoApproved));
        }

        return records;
    }

    private static string NormalizePathSlashes(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('/', '\\');
    }

    private static string CreateDecisionPrompt(
        AgentActionDecisionRequest request,
        RuntimeCommandEnvironment environment,
        string responseLanguageInstruction,
        string knowledgeContext = "",
        string workspaceContext = "",
        string templateContext = "")
    {
        var knowledgeSection = string.IsNullOrWhiteSpace(knowledgeContext)
            ? string.Empty
            : $"""

            Available knowledge (previously learned commands):
            {knowledgeContext}
            """;

        var workspaceSection = string.IsNullOrWhiteSpace(workspaceContext)
            ? string.Empty
            : $"""

            Current workspace context:
            {workspaceContext}
            """;

        var templatesSection = string.IsNullOrWhiteSpace(templateContext)
            ? string.Empty
            : $"""

            Available project templates (use operationKind ProjectScaffold with templateId):
            {templateContext}
            """;

        return $$"""
            You are a task execution agent on {{environment.OS}} ({{environment.Shell}}).
            Respond ONLY with valid JSON and no markdown.
            Output reasoningSummary and completionMessage in English only.
            - You MUST execute actions to complete the objective. Thinking alone does nothing.
            - Execute ONE action per response using the action field below.
            - After FileWrite/ScriptContent, run Test-Path or Get-ChildItem to verify.
            - After TerminalCommand, verify the output matches the objective.
            - Set isComplete=true ONLY after at least one action was executed and verified with real output.
            - Never set isComplete=true on the first response - you must execute steps first.
            - Never repeat the exact same failed command.
            {{knowledgeSection}}
            {{workspaceSection}}
            {{templatesSection}}
            Valid operationKinds: TerminalCommand, FileWrite, FileRead, ScriptContent, ScriptExecution, ProjectScaffold, PlannedPatch.
            For ScriptContent (scripts): provide content, targetPath with actual .py/.cs path, and language.
            For FileWrite (text/markdown/json): provide content, targetPath with actual .txt/.md/.json path.
            For FileRead: provide targetPath with the absolute file path (command is optional).
            For TerminalCommand/ScriptExecution: provide the command.
            For ProjectScaffold (creating a new project): provide templateId (e.g. dotnet-console, dotnet-api, python-script, python-package, node-cli) and targetPath as the project directory. Use the Available project templates list above when present.
            For PlannedPatch (changing or creating multiple files at once): provide targetPath as the root directory and plannedFiles with one {path, content} object per file; path must be relative to that root (e.g. "src/App.cs"). Prefer PlannedPatch over many separate FileWrite steps.
            For large projects, first create a PROJECT_SPEC.md file (FileWrite) with requirements and architecture BEFORE writing code files.
            targetPath must be a real absolute path.
            Do NOT use variables like $CurrentDirectory or relative paths.
            Your working directory is: {{environment.WorkingDirectory}}
            Use forward slashes in all paths (C:/Users/Name/file.py not C:\Users\Name\file.py).
            Use Windows PowerShell commands (Get-ChildItem, Test-Path, New-Item, etc.).
            Do not use Unix commands (ls, rm, grep, cat, chmod) on Windows.

            INSPECTING EXISTING CODE OR BACKUPS (read-only analysis):
            - There is NO special "analyze"/"inspect" tool. Use real commands only.
            - To list a directory (including outside the workspace, e.g. D:/Dev/Backup): use a TerminalCommand like:
              Get-ChildItem -LiteralPath "D:/Dev/Backup" -Force | Select-Object Name, Length, Extension
              or Get-ChildItem -LiteralPath "D:/Dev/Backup" -Recurse -Force -File | Select-Object FullName
            - To read a file: prefer operationKind FileRead with targetPath set to the absolute file path (works inside and outside the workspace; sensitive files like .env, keys and credentials remain blocked).
            - Read-only inspection never requires approval unless the target is a sensitive file or an operating system root (Windows, Program Files).
            - Never invent tool names: if the previous attempt used a command that does not exist, retry with Get-ChildItem/Get-Content or FileRead instead.

            IMPORTANT: In JSON, escape every backslash as double backslash.
            For example, path C:\Users\Name is written as "C:\\Users\\Name" in JSON.

            EXAMPLES - follow these patterns exactly:

            Example 1 (create a file, then verify):
            {
              "reasoningSummary": "Creating hello.py and verifying it exists.",
              "isComplete": false,
              "completionMessage": "",
              "action": {
                "objective": "Create hello.py that prints hello world",
                "operationKind": "FileWrite",
                "command": "",
                "content": "print('hello world')",
                "targetPath": "C:/Users/Name/hello.py",
                "language": "python",
                "workingDirectory": "",
                "retryJustification": "",
                "requiresSafetyReview": true
              }
            }

            Example 2 (scaffold a full project from a template):
            {
              "reasoningSummary": "Scaffolding a new .NET console project from the dotnet-console template.",
              "isComplete": false,
              "completionMessage": "",
              "action": {
                "objective": "Create a new .NET console project",
                "operationKind": "ProjectScaffold",
                "command": "",
                "templateId": "dotnet-console",
                "targetPath": "C:/Users/Name/MyProject",
                "language": "",
                "workingDirectory": "",
                "retryJustification": "",
                "requiresSafetyReview": true
              }
            }

            Example 3 (finish only after evidence):
            {
              "reasoningSummary": "hello.py exists and prints hello world.",
              "isComplete": true,
              "completionMessage": "Created hello.py and verified it with Get-ChildItem.",
              "action": null
            }

            Example 4 (planned multi-file patch):
            {
              "reasoningSummary": "Applying a planned patch that updates the app and its docs.",
              "isComplete": false,
              "completionMessage": "",
              "action": {
                "objective": "Update the app and its docs",
                "operationKind": "PlannedPatch",
                "command": "",
                "targetPath": "C:/Users/Name/MyProject",
                "plannedFiles": [
                  {
                    "path": "src/App.cs",
                    "content": "public class App { }"
                  },
                  {
                    "path": "README.md",
                    "content": "# My Project"
                  }
                ],
                "language": "",
                "workingDirectory": "",
                "retryJustification": "",
                "requiresSafetyReview": true
              }
            }

            Use this format:
            {
              "reasoningSummary": "what you are doing (1 sentence)",
              "isComplete": false,
              "completionMessage": "",
              "action": {
                "objective": "what this step does",
                "operationKind": "TerminalCommand|FileWrite|FileRead|ScriptContent|ScriptExecution|ProjectScaffold|PlannedPatch",
                "command": "the shell command",
                "content": "file content for FileWrite/ScriptContent",
                "targetPath": "path for file operations (use \\\\)",
                "templateId": "template id for ProjectScaffold",
                "plannedFiles": "[{path, content}] for PlannedPatch (paths relative to targetPath)",
                "language": "python|csharp|text",
                "workingDirectory": "",
                "retryJustification": "",
                "requiresSafetyReview": true
              }
            }
            Set action to null when isComplete: true.

            Objective: {{request.Objective}}
            Chat history:
            {{request.ChatHistoryContext}}
            Plan and progress:
            {{request.CurrentPlan}}
            Previous result:
            {{request.PreviousActionResult ?? "none"}}
            Observations:
            {{AgentActionSession.BuildObservationContext(request.Observations)}}
            History:
            {{ExecutionHistory.BuildContext(request.ExecutionHistory)}}
            Step {{request.StepNumber}} Retry {{request.RetryNumber}}
            """;
    }

    private async Task<string> BuildWorkspaceContextAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (workspaceMapService is null && workspaceMemoryService is null)
        {
            return string.Empty;
        }

        var sections = new List<string>();
        try
        {
            if (workspaceMapService is not null)
            {
                var map = await workspaceMapService.BuildAsync(
                    workspaceRoot,
                    cancellationToken);
                var summary = map.BuildSummary();
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    sections.Add(summary);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[AGENT] Workspace map failed (non-fatal): {ex.Message}");
        }

        if (workspaceMemoryService is not null)
        {
            try
            {
                var memorySummary = await workspaceMemoryService.BuildSummaryAsync(
                    workspaceRoot,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(memorySummary))
                {
                    sections.Add(memorySummary);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Log($"[AGENT] Workspace memory failed (non-fatal): {ex.Message}");
            }
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            sections);
    }

    private string BuildTemplateContext()
    {
        if (projectTemplateCatalog is null)
        {
            return string.Empty;
        }

        var lines = projectTemplateCatalog
            .GetAll()
            .Select(template =>
                $"- {template.Id}: {template.Name} ({template.Stack}). {template.Description}");
        return string.Join(Environment.NewLine, lines);
    }

    private void LogRawResponse(string rawResponse, AgentActionDecisionRequest request)
    {
        try
        {
            var truncated = rawResponse.Length > 2000
                ? rawResponse[..2000] + "..."
                : rawResponse;
            logger.Log($"[AGENT] Raw LLM response: {truncated}");
        }
        catch
        {
            // best effort
        }
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
        decision.Action.TargetPath = NormalizePathSlashes(decision.Action.TargetPath);
        decision.Action.Language = decision.Action.Language?.Trim();
    }

    private static string TruncateForError(string text, int maxLen = 500)
    {
        return text.Length <= maxLen
            ? text
            : text[..maxLen] + "...";
    }

    private static string TruncateForLog(string text, int maxLen = 80)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length <= maxLen
            ? text
            : text[..maxLen] + "...";
    }

    private static bool HasValidAction(AgentToolAction? action)
    {
        if (action is null || string.IsNullOrWhiteSpace(action.Objective))
        {
            return false;
        }

        if (action.OperationKind == OperationKind.ProjectScaffold)
        {
            return !string.IsNullOrWhiteSpace(action.TemplateId) ||
                   !string.IsNullOrWhiteSpace(action.TargetPath);
        }

        if (action.OperationKind == OperationKind.PlannedPatch)
        {
            return action.PlannedFiles is { Count: > 0 };
        }

        if (action.OperationKind == OperationKind.FileRead)
        {
            return !string.IsNullOrWhiteSpace(action.TargetPath) ||
                   !string.IsNullOrWhiteSpace(action.Command);
        }

        return !string.IsNullOrWhiteSpace(action.Command) ||
               (!string.IsNullOrWhiteSpace(action.Content) &&
                !string.IsNullOrWhiteSpace(action.TargetPath));
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

    private static bool ValidateTargetPath(string targetPath, out string issue)
    {
        if (targetPath.Length >= 2 && targetPath[1] == ':')
        {
            var driveLetter = targetPath[0];
            if (!char.IsLetter(driveLetter))
            {
                issue = $"Invalid drive letter in path: '{targetPath}'";
                return false;
            }

            var driveRoot = $"{driveLetter}:\\";
            try
            {
                if (!Directory.Exists(driveRoot))
                {
                    issue = $"Drive '{driveRoot}' does not exist on this system. " +
                            "Please verify the drive letter and correct the command.";
                    return false;
                }
            }
            catch
            {
                issue = $"Could not verify drive '{driveRoot}'.";
                return false;
            }
        }

        issue = string.Empty;
        return true;
    }

    private async Task<string> AutoVerifyFileSystemAsync(
        string targetPath,
        CommandExecution execution,
        AgentActionSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var verifyCommand = OperatingSystem.IsWindows()
                ? $"powershell.exe -Command \"Test-Path -LiteralPath '{targetPath.Replace("'", "''")}'\""
                : $"test -f '{targetPath.Replace("'", "'\\''")}' || test -d '{targetPath.Replace("'", "'\\''")}'";

            var verifyOutput = await executor.RunCommandAsync(
                verifyCommand, cancellationToken);

            var exists = OperatingSystem.IsWindows()
                ? verifyOutput.Trim().Equals("True", StringComparison.OrdinalIgnoreCase)
                : verifyOutput.Trim() == "0" ||
                  (verifyOutput.Contains("exists", StringComparison.OrdinalIgnoreCase) &&
                   !verifyOutput.Contains("No such file", StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                var evidence = evidenceCollector.Collect(
                    new ExecutionEvidenceInput(
                        session.Request.ConversationId,
                        execution.StepId,
                        OperationKind.TerminalCommand,
                        FilePath: targetPath,
                        Executed: true,
                        ExitCode: 0,
                        StdOut: $"File exists: {targetPath}",
                        Success: true));
                session.RecordEvidence(evidence);
                logger.Log($"[AGENT] Auto-verify: '{targetPath}' exists.");
                return $"[Verified: '{targetPath}' foi criado com sucesso.]";
            }

            logger.Log($"[AGENT] Auto-verify: '{targetPath}' NOT FOUND after write.");
            return $"[Verification failed: '{targetPath}' nao foi encontrado apos a escrita.]";
        }
        catch (Exception ex)
        {
            logger.Log($"[AGENT] Auto-verify command failed: {ex.Message}");
            return string.Empty;
        }
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
