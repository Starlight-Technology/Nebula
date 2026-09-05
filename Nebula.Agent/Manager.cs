using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Core.Agent;
using Nebula.Core.Interactions;
using Nebula.Core.Memory;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Runner;
using Nebula.Services.Safety;

namespace Nebula.Agent;

public class Manager : IManager
{
    private readonly ILlamaClient llamaClient;
    private readonly ILogger logger;
    private readonly ICommandPolicyEngine commandPolicyEngine;
    private readonly IAgentActionRunner actionRunner;
    private readonly ChatResponseService chatResponseService;
    private readonly IConversationContextService conversationContextService;
    private readonly PromptRequestAuditService promptAuditService;
    private readonly int maxActionRetryCount;
    private readonly int maxActionStepCount;
    private readonly ConcurrentDictionary<Guid, HashSet<string>>
        approvedCommandsByConversation = new();
    private Guid activeConversationId = Guid.NewGuid();

    public Manager(
        ILlamaClient llamaClient,
        IShellExecutor executor,
        IJsonExtractor jsonExtractor,
        ILogger logger,
        ICommandRepository? commandRepository = null,
        IPromptRequestRepository? promptRepository = null,
        IConversationMemoryRepository? conversationMemoryRepository = null,
        NebulaContextBuilder? contextBuilder = null,
        int maxActionRetries = AgentActionRunRequest.DefaultMaxRetriesPerStep,
        int maxActionSteps = AgentActionRunRequest.DefaultMaxSteps,
        IAgentActionRunner? actionRunner = null,
        IConversationContextService? conversationContextService = null,
        ICommandPolicyEngine? commandPolicyEngine = null)
    {
        this.llamaClient = llamaClient;
        this.logger = logger;
        this.commandPolicyEngine = commandPolicyEngine ?? CreateDefaultPolicyEngine(logger);
        this.actionRunner = actionRunner ?? new AgentActionRunner(
            llamaClient,
            executor,
            jsonExtractor,
            logger,
            commandRepository,
            maxActionRetries,
            commandPolicyEngine: this.commandPolicyEngine);

        var conversationContextBuilder = contextBuilder ?? new NebulaContextBuilder();
        chatResponseService = new ChatResponseService(llamaClient);
        this.conversationContextService = conversationContextService
            ?? new ConversationContextService(
                conversationMemoryRepository,
                conversationContextBuilder,
                logger);
        promptAuditService = new PromptRequestAuditService(promptRepository, logger);
        maxActionRetryCount = Math.Max(0, maxActionRetries);
        maxActionStepCount = Math.Max(1, maxActionSteps);
    }

    public Guid ActiveConversationId => activeConversationId;

    public async Task<string> ManageResponse(UserMessage message)
    {
        var turn = await ManageConversationAsync(message);
        return turn.Response;
    }

    public Task<ConversationTurn> ManageConversationAsync(UserMessage message)
    {
        return ManageConversationAsync(message, progress: null, cancellationToken: default);
    }

    public Guid StartNewConversation()
    {
        activeConversationId = Guid.NewGuid();
        logger.Log($"Started new ConversationId '{activeConversationId}'.");
        return activeConversationId;
    }

    public Guid SelectConversation(Guid conversationId)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("Conversation id cannot be empty.", nameof(conversationId));
        }

        activeConversationId = conversationId;
        logger.Log($"Switched to ConversationId '{activeConversationId}'.");
        return activeConversationId;
    }

    public async Task<ConversationTurn> ManageConversationAsync(
        UserMessage message,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return CreateEmptyPromptTurn(message);
        }

        var request = new ConversationRequest(
            activeConversationId,
            Guid.NewGuid(),
            message with { Content = message.Content.Trim() },
            llamaClient.SelectedModel)
        {
            ConversationApprovedCommands =
                approvedCommandsByConversation.TryGetValue(
                    activeConversationId,
                    out var approved)
                    ? approved.ToList()
                    : null
        };

        try
        {
            return await ProcessConversationAsync(request, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Log(
                $"{ModePrefix(request.Mode)} Request '{request.RequestId}' for " +
                $"ConversationId '{request.ConversationId}' was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"{ModePrefix(request.Mode)} Error managing response: {ex.Message}");
#if DEBUG
            var debugInfo = $"**{ModePrefix(request.Mode)} Erro:** {ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
            return new ConversationTurn
            {
                ConversationId = request.ConversationId,
                RequestId = request.RequestId,
                Prompt = request.Prompt,
                Mode = request.Mode,
                ModelName = llamaClient.SelectedModel,
                Response = debugInfo,
                ActionStatus = ActionExecutionStatus.Failed,
                ActionEvents =
                [
                    new ActionExecutionEvent
                    {
                        Kind = ActionExecutionEventKind.Failed,
                        Status = ActionExecutionStatus.Failed,
                        Title = "Debug error",
                        Message = debugInfo
                    }
                ],
                IsCancelled = false
            };
#else
            throw;
#endif
        }
    }

    public Task<ConversationTurn> RunApprovedCommandAsync(
        CommandExecution command,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default) =>
        RunApprovedCommandAsync(
            command,
            progress,
            ApprovalScope.Once,
            cancellationToken);

    public Task<ConversationTurn> RunApprovedCommandAsync(
        CommandExecution command,
        IProgress<ConversationTurn>? progress,
        ApprovalScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Run);
        return RunApprovedCommandCoreAsync(
            command,
            progress,
            scope,
            cancellationToken);
    }

    private async Task<ConversationTurn> RunApprovedCommandCoreAsync(
        CommandExecution command,
        IProgress<ConversationTurn>? progress,
        ApprovalScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Run);

        var prompt = $"Executar comando aprovado: {command.Run}";
        logger.Log(
            $"[AGENT] User approved command for ConversationId '{activeConversationId}' " +
            $"(scope {scope}): {command.Run}");

        var normalized = CommandNormalization.Normalize(command.Run);
        if (scope == ApprovalScope.Conversation)
        {
            approvedCommandsByConversation
                .GetOrAdd(activeConversationId, static _ => new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase))
                .Add(normalized);
        }

        var requestId = Guid.NewGuid();
        var promptRequest = new PromptRequest
        {
            Id = requestId,
            Prompt = prompt,
            Mode = InteractionMode.Agent,
            Classification = InteractionMode.Agent.ToString()
        };
        await promptAuditService.SaveAsync(promptRequest, cancellationToken);

        return await actionRunner.RunAsync(
            new AgentActionRunRequest
            {
                ConversationId = activeConversationId,
                RequestId = requestId,
                Prompt = prompt,
                Mode = InteractionMode.Agent,
                ChatHistoryContext =
                    $"[approved_command]\nObjective: {command.Objective}\nCommand: {command.Run}",
                ModelName = llamaClient.SelectedModel,
                MaxSteps = 1,
                MaxRetriesPerStep = 0,
                ConversationApprovedCommands =
                    approvedCommandsByConversation.TryGetValue(
                        activeConversationId,
                        out var approved)
                        ? approved.ToList()
                        : null,
                ApprovedAction = new AgentApprovedAction
                {
                    Objective = command.Objective,
                    Command = command.Run,
                    OperationKind = command.OperationKind,
                    TargetPath = command.TargetPath,
                    PlannedFiles = command.PlannedFiles,
                    Content = command.Content,
                    Language = command.Language,
                    TemplateId = command.TemplateId,
                    WorkingDirectory = command.WorkingDirectory,
                    Scope = scope
                }
            },
            progress,
            cancellationToken);
    }

    public Task<ConversationTurn> ResumeTaskAsync(
        AgentRun run,
        IProgress<ConversationTurn>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        logger.Log(
            $"[AGENT] Resuming task '{run.Prompt}' for ConversationId '{activeConversationId}' " +
            $"from run '{run.Id}' (previous status: {run.Status}).");

        return actionRunner.RunAsync(
            new AgentActionRunRequest
            {
                ConversationId = activeConversationId,
                RequestId = Guid.NewGuid(),
                Prompt = run.Prompt,
                Mode = InteractionMode.Agent,
                ChatHistoryContext = BuildResumeContext(run),
                ModelName = string.IsNullOrWhiteSpace(run.ModelName)
                    ? llamaClient.SelectedModel
                    : run.ModelName,
                MaxSteps = maxActionStepCount,
                MaxRetriesPerStep = maxActionRetryCount,
                WorkspaceRoot = run.WorkspaceRoot,
                ConversationApprovedCommands =
                    approvedCommandsByConversation.TryGetValue(
                        activeConversationId,
                        out var approved)
                        ? approved.ToList()
                        : null
            },
            progress,
            cancellationToken);
    }

    public async Task<bool> VerifyCommandCorrectAsync(Command command)
    {
        var response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". This following command {{command.Run}} execute EXACTLY {{command.Objective}} on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}? Command: {{command.Run}}
            """);

        return IsAffirmativeResponse(response);
    }

    public async Task<bool> VerifyCommandSafetyAsync(Command command)
    {
        var decision = await commandPolicyEngine.EvaluateAsync(command.Run);
        return decision.Decision == CommandSafetyDecisionType.Allow;
    }

    public async Task<string> GenerateCommandSteps(string userRequest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userRequest);

        var plannerPrompt = CommandPromptFactory.CreatePlanPrompt(userRequest, userRequest);
        return await llamaClient.GetResponseAsync(plannerPrompt);
    }

    private async Task<ConversationTurn> ProcessConversationAsync(
        ConversationRequest request,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken)
    {
        logger.Log(
            $"{ModePrefix(request.Mode)} Using ConversationId '{request.ConversationId}' " +
            $"for request '{request.RequestId}'.");

        var conversationContext = await conversationContextService.PrepareAsync(
            request.ConversationId,
            request.Prompt,
            request.Mode,
            cancellationToken);
        var promptRequest = request.CreatePromptRequest();

        await promptAuditService.SaveAsync(promptRequest, cancellationToken);

        var turn = await CreateTurnAsync(
            request,
            conversationContext.ModelPrompt,
            progress,
            cancellationToken);

        await CompleteConversationAsync(
            request,
            conversationContext,
            promptRequest,
            turn,
            cancellationToken);

        return turn;
    }

    private Task<ConversationTurn> CreateTurnAsync(
        ConversationRequest request,
        string modelPrompt,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken)
    {
        return request.Mode switch
        {
            InteractionMode.Agent => actionRunner.RunAsync(
                request.CreateActionRequest(
                    modelPrompt,
                    maxActionStepCount,
                    maxActionRetryCount),
                progress,
                cancellationToken),
            InteractionMode.Chat => chatResponseService.GetResponseAsync(
                request,
                modelPrompt,
                progress,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request.Mode),
                request.Mode,
                "Unsupported interaction mode.")
        };
    }

    private async Task CompleteConversationAsync(
        ConversationRequest request,
        PreparedConversationContext conversationContext,
        PromptRequest promptRequest,
        ConversationTurn turn,
        CancellationToken cancellationToken)
    {
        turn.ConversationId = request.ConversationId;
        promptRequest.Response = turn.Response;
        promptRequest.UpdatedAt = DateTime.UtcNow;

        await promptAuditService.UpdateResponseAsync(
            request.RequestId,
            turn.Response,
            request.Mode,
            cancellationToken);
        await conversationContextService.CompleteAsync(
            conversationContext,
            request.Prompt,
            turn,
            cancellationToken);
    }

    private ConversationTurn CreateEmptyPromptTurn(UserMessage message)
    {
        return new ConversationTurn
        {
            ConversationId = activeConversationId,
            RequestId = Guid.Empty,
            Prompt = message.Content,
            Mode = message.Mode,
            ModelName = llamaClient.SelectedModel,
            Classification = message.Mode.ToString(),
            Response = "The prompt are empty, write something."
        };
    }

    private static string ModePrefix(InteractionMode mode) =>
        mode == InteractionMode.Agent ? "[AGENT]" : "[CHAT]";

    private static string BuildResumeContext(AgentRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[resumed_task]");
        sb.AppendLine(
            "This task was interrupted and is being resumed. Continue from where it stopped " +
            "using the plan and the last observation below. Do not restart from scratch.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(run.CurrentPlan))
        {
            sb.AppendLine("Previous plan:");
            sb.AppendLine(run.CurrentPlan);
            sb.AppendLine();
        }

        var lastStep = run.Steps.LastOrDefault();
        if (lastStep is not null)
        {
            sb.AppendLine(
                $"Last step: {lastStep.Step}.{lastStep.Attempt} {lastStep.Objective} " +
                $"(success: {lastStep.Success})");
            if (!string.IsNullOrWhiteSpace(lastStep.Command))
            {
                sb.AppendLine($"Last command: {lastStep.Command}");
            }

            if (!string.IsNullOrWhiteSpace(lastStep.StandardOutput))
            {
                sb.AppendLine($"Last output: {lastStep.StandardOutput}");
            }

            if (!string.IsNullOrWhiteSpace(lastStep.StandardError))
            {
                sb.AppendLine($"Last error: {lastStep.StandardError}");
            }
        }

        return sb.ToString();
    }

    private static ICommandPolicyEngine CreateDefaultPolicyEngine(ILogger logger)
    {
        var deterministic = new DeterministicCommandClassifier();
        var ml = new MlNetCommandClassifier();
        return new CommandPolicyEngine(
            new CompositeCommandClassifier(deterministic, ml),
            message => logger.Log($"[AGENT] {message}"));
    }

    private static bool IsAffirmativeResponse(string rawResponse)
    {
        var response = ModelResponse.Parse(rawResponse).Response.Trim();
        return Regex.IsMatch(response, @"^yes\b", RegexOptions.IgnoreCase);
    }
}
