using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nebula.Agent.Application;
using Nebula.Agent.Application.Events;
using Nebula.Agent.Data;
using Nebula.Agent.Infrastructure.Events;
using Nebula.Agent.Infrastructure.Execution;
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
    private readonly IAgentRunOrchestrator orchestrator;
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
        ICommandPolicyEngine? commandPolicyEngine = null,
        IAgentRunOrchestrator? orchestrator = null)
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
        this.orchestrator = orchestrator ?? CreateDefaultOrchestrator();

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

    private IAgentRunOrchestrator CreateDefaultOrchestrator()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAgentRunEventPublisher, ChannelAgentRunEventPublisher>()
            .AddSingleton<IAgentRunQueue, AgentRunBackgroundService>()
            .AddScoped<IAgentRunOrchestrator, AgentRunOrchestrator>();
        var serviceProvider = services.BuildServiceProvider();
        return serviceProvider.GetRequiredService<IAgentRunOrchestrator>();
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

        // Prepare conversation context for memory update
        var userMessage = new UserMessage(prompt, InteractionMode.Agent);
        var conversationRequest = new ConversationRequest(
            activeConversationId,
            requestId,
            userMessage,
            llamaClient.SelectedModel);
        var conversationContext = await conversationContextService.PrepareAsync(
            activeConversationId,
            prompt,
            InteractionMode.Agent,
            cancellationToken);

        // Manter mesmo ConversationId e workspace base para que o arquivo aprovado caia na mesma pasta <id8> do chat
        var workspaceRoot = !string.IsNullOrWhiteSpace(command.WorkingDirectory)
            ? command.WorkingDirectory
            : command.TargetPath;
        var runId = await orchestrator.RunApprovedCommandAsync(command, scope, activeConversationId, workspaceRoot, cancellationToken);

        if (progress != null)
        {
            _ = BridgeTypedEventsToLegacyProgress(runId, progress, conversationContext, conversationRequest, cancellationToken);
        }

        return new ConversationTurn
        {
            ConversationId = activeConversationId,
            RequestId = requestId,
            Prompt = prompt,
            Mode = InteractionMode.Agent,
            Classification = InteractionMode.Agent.ToString(),
            ModelName = llamaClient.SelectedModel,
            ActionStatus = ActionExecutionStatus.Started,
            Response = "Approved command started in background..."
        };
    }

    public async Task<ConversationTurn> ResumeTaskAsync(
        AgentRun run,
        IProgress<ConversationTurn>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        logger.Log(
            $"[AGENT] Resuming task '{run.Prompt}' for ConversationId '{activeConversationId}' " +
            $"from run '{run.Id}' (previous status: {run.Status}).");

        // Prepare conversation context for memory update
        var resumePrompt = $"Retomar tarefa: {run.Prompt}";
        var userMessage = new UserMessage(resumePrompt, InteractionMode.Agent);
        var requestId = Guid.NewGuid();
        var conversationRequest = new ConversationRequest(
            activeConversationId,
            requestId,
            userMessage,
            llamaClient.SelectedModel);
        var conversationContext = await conversationContextService.PrepareAsync(
            activeConversationId,
            resumePrompt,
            InteractionMode.Agent,
            cancellationToken);

        var runId = await orchestrator.ResumeAsync(run, cancellationToken);

        if (progress != null)
        {
            _ = BridgeTypedEventsToLegacyProgress(runId, progress, conversationContext, conversationRequest, cancellationToken);
        }

        return new ConversationTurn
        {
            ConversationId = activeConversationId,
            RequestId = requestId,
            Prompt = resumePrompt,
            Mode = InteractionMode.Agent,
            Classification = InteractionMode.Agent.ToString(),
            ModelName = llamaClient.SelectedModel,
            ActionStatus = ActionExecutionStatus.Started,
            Response = "Task resume started in background..."
        };
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
            cancellationToken,
            conversationContext);

        await CompleteConversationAsync(
            request,
            conversationContext,
            promptRequest,
            turn,
            cancellationToken);

        return turn;
    }

    private async Task<ConversationTurn> CreateTurnAsync(
        ConversationRequest request,
        string modelPrompt,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken,
        PreparedConversationContext conversationContext)
    {
        return request.Mode switch
        {
            InteractionMode.Agent => await CreateAgentTurnAsync(request, modelPrompt, progress, cancellationToken, conversationContext),
            InteractionMode.Chat => await chatResponseService.GetResponseAsync(
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

    private async Task<ConversationTurn> CreateAgentTurnAsync(
        ConversationRequest request,
        string modelPrompt,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken,
        PreparedConversationContext conversationContext)
    {
        var actionRequest = request.CreateActionRequest(
            conversationContext.ChatHistoryContext,
            maxActionStepCount,
            maxActionRetryCount);

        actionRequest.ProviderMode = request.Message.ProviderMode;

        var selector = new AgentProviderSelector();
        var selectedAgent = selector.Select(AgentCapability.Coding, actionRequest.ProviderMode);
        actionRequest.SelectedAgentId = selectedAgent?.Id;
        actionRequest.SelectedAgentName = selectedAgent?.Name;
        actionRequest.SelectedAgentDescription = selectedAgent?.Description;

        var runId = await orchestrator.RunAsync(actionRequest, cancellationToken);

        if (progress != null)
        {
            _ = BridgeTypedEventsToLegacyProgress(runId, progress, conversationContext, request, cancellationToken);
        }

        var reasoning = selectedAgent is null
            ? "Iniciando a tarefa localmente e validando o objetivo antes de executar qualquer comando."
            : $"Iniciando a tarefa com o agente '{selectedAgent.Name}' ({selectedAgent.PreferredMode}). Vou validar o objetivo, decompor a ação e executar somente o necessário em ambiente local.";

        var response = selectedAgent is null
            ? "Agent run started in background..."
            : $"Agent run started in background using '{selectedAgent.Name}' ({selectedAgent.PreferredMode}).";

        return new ConversationTurn
        {
            ConversationId = request.ConversationId,
            RequestId = request.RequestId,
            Prompt = request.Prompt,
            Mode = InteractionMode.Agent,
            Classification = InteractionMode.Agent.ToString(),
            ModelName = llamaClient.SelectedModel,
            SelectedAgentId = actionRequest.SelectedAgentId ?? string.Empty,
            SelectedAgentName = actionRequest.SelectedAgentName ?? string.Empty,
            ActionStatus = ActionExecutionStatus.Started,
            Response = response,
            Reasoning = reasoning,
            CurrentPlan = "#1 [pending] [risk=low] Validar objetivo e iniciar a execução local."
        };
    }

    private async Task BridgeTypedEventsToLegacyProgress(
        Guid runId,
        IProgress<ConversationTurn> legacyProgress,
        PreparedConversationContext conversationContext,
        ConversationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in orchestrator.SubscribeAsync(runId, cancellationToken))
            {
                if (evt is AgentRunCompletedEvent completed)
                {
                    legacyProgress.Report(completed.FinalTurn);
                    
                    // Update conversation memory with the actual final turn (not the initial "started" message)
                    await conversationContextService.CompleteAsync(
                        conversationContext,
                        request.Prompt,
                        completed.FinalTurn,
                        cancellationToken);
                    break;
                }
                else if (evt is AgentRunFailedEvent failed)
                {
                    var failedTurn = failed.PartialTurn ?? BuildFailedTurn(failed.ErrorMessage);
                    legacyProgress.Report(failedTurn);
                    
                    // Update conversation memory with the failure turn
                    await conversationContextService.CompleteAsync(
                        conversationContext,
                        request.Prompt,
                        failedTurn,
                        cancellationToken);
                    break;
                }
                else if (evt is AgentRunCancelledEvent)
                {
                    var cancelledTurn = new ConversationTurn
                    {
                        ConversationId = activeConversationId,
                        RequestId = Guid.NewGuid(),
                        Prompt = "",
                        Mode = InteractionMode.Agent,
                        ModelName = llamaClient.SelectedModel,
                        Response = "Execution cancelled by user.",
                        ActionStatus = ActionExecutionStatus.Cancelled,
                        IsCancelled = true
                    };
                    legacyProgress.Report(cancelledTurn);
                    
                    // Update conversation memory with the cancellation turn
                    await conversationContextService.CompleteAsync(
                        conversationContext,
                        request.Prompt,
                        cancelledTurn,
                        cancellationToken);
                    break;
                }
                else
                {
                    var partialTurn = BuildPartialTurnFromEvent(evt);
                    legacyProgress.Report(partialTurn);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError($"Error in BridgeTypedEventsToLegacyProgress: {ex.Message}");
        }
    }

    private static ConversationTurn BuildFailedTurn(string errorMessage)
    {
        return new ConversationTurn
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "",
            Mode = InteractionMode.Agent,
            ModelName = "",
            Response = $"Agent run failed: {errorMessage}",
            ActionStatus = ActionExecutionStatus.Failed
        };
    }

    private static ConversationTurn BuildPartialTurnFromEvent(AgentRunEvent evt)
    {
        return evt switch
        {
            AgentRunStepStartedEvent e => new ConversationTurn
            {
                ConversationId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                Prompt = e.Objective,
                Mode = InteractionMode.Agent,
                ModelName = "",
                ActionStatus = ActionExecutionStatus.Executing,
                Response = e.Objective,
                ActionEvents =
                [
                    new ActionExecutionEvent
                    {
                        Kind = ActionExecutionEventKind.ActionStarted,
                        Status = ActionExecutionStatus.Executing,
                        Step = e.Step,
                        Attempt = e.Attempt,
                        Title = "Action started",
                        Message = e.Objective,
                        Command = e.Command
                    }
                ]
            },
            AgentRunCommandOutputEvent e => new ConversationTurn
            {
                ConversationId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                Prompt = "",
                Mode = InteractionMode.Agent,
                ModelName = "",
                ActionStatus = ActionExecutionStatus.Executing,
                Response = e.Chunk,
                ActionEvents =
                [
                    new ActionExecutionEvent
                    {
                        Kind = ActionExecutionEventKind.StreamOutput,
                        Status = ActionExecutionStatus.Executing,
                        Step = e.Step,
                        Attempt = e.Attempt,
                        Title = "Stream output",
                        Message = e.Chunk,
                        IsError = e.IsError
                    }
                ]
            },
            AgentRunStepCompletedEvent e => new ConversationTurn
            {
                ConversationId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                Prompt = "",
                Mode = InteractionMode.Agent,
                ModelName = "",
                ActionStatus = ActionExecutionStatus.Observing,
                Response = e.Observation,
                ActionEvents =
                [
                    new ActionExecutionEvent
                    {
                        Kind = ActionExecutionEventKind.Observation,
                        Status = ActionExecutionStatus.Observing,
                        Step = e.Step,
                        Attempt = e.Attempt,
                        Title = "Observation",
                        Message = e.Observation
                    }
                ]
            },
            AgentRunPlanUpdatedEvent e => new ConversationTurn
            {
                ConversationId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                Prompt = "",
                Mode = InteractionMode.Agent,
                ModelName = "",
                ActionStatus = ActionExecutionStatus.Planning,
                Response = "",
                CurrentPlan = BuildPlanString(e.Plan)
            },
            AgentRunApprovalRequiredEvent e => new ConversationTurn
            {
                ConversationId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                Prompt = "",
                Mode = InteractionMode.Agent,
                ModelName = "",
                ActionStatus = ActionExecutionStatus.AwaitingApproval,
                Response = e.Reason,
                ActionEvents =
                [
                    new ActionExecutionEvent
                    {
                        Kind = ActionExecutionEventKind.ApprovalRequired,
                        Status = ActionExecutionStatus.AwaitingApproval,
                        Step = e.Step,
                        Attempt = e.Attempt,
                        Title = "Approval required",
                        Message = e.Reason,
                        Command = e.Command.Run
                    }
                ],
                Commands = [e.Command]
            },
            AgentRunStepRetryEvent e => new ConversationTurn
            {
                ConversationId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                Prompt = "",
                Mode = InteractionMode.Agent,
                ModelName = "",
                ActionStatus = ActionExecutionStatus.Retrying,
                Response = e.FailureReason,
                ActionEvents =
                [
                    new ActionExecutionEvent
                    {
                        Kind = ActionExecutionEventKind.RetryScheduled,
                        Status = ActionExecutionStatus.Retrying,
                        Step = e.Step,
                        Attempt = e.Attempt,
                        Title = "Retry scheduled",
                        Message = e.FailureReason
                    }
                ]
            },
            _ => new ConversationTurn
            {
                ConversationId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                Prompt = "",
                Mode = InteractionMode.Agent,
                ModelName = "",
                ActionStatus = ActionExecutionStatus.Executing,
                Response = ""
            }
        };
    }

    private static string BuildPlanString(IReadOnlyList<AgentPlanStep> plan)
    {
        if (plan.Count == 0)
        {
            return "No actions completed yet.";
        }

        var planLines = plan
            .OrderBy(step => step.Id)
            .Select(step =>
            {
                var deps = step.DependsOn.Count == 0
                    ? string.Empty
                    : $" (depends on {string.Join(",", step.DependsOn)})";
                var checkpoint = step.IsCheckpoint ? " [checkpoint]" : string.Empty;
                return $"#{step.Id} [{step.Status}]{checkpoint} [risk={step.Risk}]{deps} {step.Description}";
            });
        return string.Join(Environment.NewLine, planLines);
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
