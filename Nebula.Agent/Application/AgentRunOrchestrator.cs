using System.Collections.Concurrent;
using System.Text;
using Nebula.Agent.Application.Events;
using Nebula.Agent.Data;
using Nebula.Core.Agent;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Projects;
using Nebula.Core.Safety;

namespace Nebula.Agent.Application;

public sealed class AgentRunOrchestrator : IAgentRunOrchestrator
{
    private readonly IAgentRunQueue _queue;
    private readonly IAgentRunEventPublisher _eventPublisher;
    private readonly IAgentRunStore? _runStore;
    private readonly ConcurrentDictionary<Guid, AgentRunStatus> _statusCache = new();

    public AgentRunOrchestrator(
        IAgentRunQueue queue,
        IAgentRunEventPublisher eventPublisher,
        IAgentRunStore? runStore = null)
    {
        _queue = queue;
        _eventPublisher = eventPublisher;
        _runStore = runStore;
    }

    public async Task<Guid> RunAsync(AgentActionRunRequest request, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid();
        request.RunId ??= runId;
        request.WorkspaceRoot = ReferenceWorkspace.Resolve(
            request.WorkspaceRoot,
            request.ConversationId).Root;
        var workItem = new AgentRunWorkItem(runId, request, cancellationToken);

        _statusCache[runId] = new AgentRunStatus(
            runId,
            "Running",
            0,
            request.MaxSteps ?? 10,
            request.Prompt,
            DateTimeOffset.UtcNow,
            null);

        if (_runStore != null)
        {
            var agentRun = new AgentRun(
                Id: runId,
                ConversationId: request.ConversationId,
                RequestId: request.RequestId,
                Prompt: request.Prompt,
                ModelName: request.ModelName,
                Status: "Running",
                StartedAt: DateTimeOffset.UtcNow,
                FinishedAt: null,
                Response: null,
                IsCancelled: false,
                Steps: [],
                CurrentPlan: "",
                Artifacts: [],
                Approvals: [],
                WorkspaceRoot: request.WorkspaceRoot,
                ParentRunId: request.ParentRunId);
            await _runStore.SaveRunAsync(agentRun, CancellationToken.None);
        }

        await _queue.EnqueueAsync(workItem, cancellationToken);
        return runId;
    }

    public async Task<Guid> RunDryRunAsync(AgentActionRunRequest request, CancellationToken cancellationToken = default)
    {
        var dryRunRequest = new AgentActionRunRequest
        {
            ConversationId = request.ConversationId,
            RequestId = request.RequestId,
            Prompt = request.Prompt,
            Mode = request.Mode,
            ChatHistoryContext = request.ChatHistoryContext,
            ModelName = request.ModelName,
            MaxSteps = request.MaxSteps,
            MaxRetriesPerStep = request.MaxRetriesPerStep,
            WorkspaceRoot = request.WorkspaceRoot,
            ParentRunId = request.ParentRunId,
            ApprovedAction = request.ApprovedAction,
            ConversationApprovedCommands = request.ConversationApprovedCommands,
            DryRun = true
        };
        return await RunAsync(dryRunRequest, cancellationToken);
    }

    public async Task<Guid> ResumeAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        var request = new AgentActionRunRequest
        {
            ConversationId = run.ConversationId,
            RequestId = Guid.NewGuid(),
            Prompt = run.Prompt,
            Mode = InteractionMode.Agent,
            ChatHistoryContext = BuildResumeContext(run),
            ModelName = run.ModelName,
            MaxSteps = 20,
            MaxRetriesPerStep = 2,
            WorkspaceRoot = run.WorkspaceRoot,
            ParentRunId = run.Id
        };
        return await RunAsync(request, cancellationToken);
    }

    public async Task<Guid> RunApprovedCommandAsync(
        CommandExecution command,
        ApprovalScope scope,
        Guid conversationId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        var isTerminalCommand = command.OperationKind == OperationKind.TerminalCommand;
        var parentRunId = _runStore is null
            ? null
            : await _runStore.GetRunIdByStepIdAsync(command.StepId, cancellationToken);
        var request = new AgentActionRunRequest
        {
            ConversationId = conversationId,
            RequestId = requestId,
            Prompt = isTerminalCommand
                ? $"Executar comando aprovado: {command.Run}"
                : $"Executar acao aprovada ({command.OperationKind}): {command.Objective}",
            Mode = InteractionMode.Agent,
            ChatHistoryContext = isTerminalCommand
                ? $"[approved_command]\nObjective: {command.Objective}\nCommand: {command.Run}"
                : $"[approved_action]\nOperation: {command.OperationKind}\nObjective: {command.Objective}\nTarget: {command.TargetPath}",
            ModelName = "",
            MaxSteps = 10,
            MaxRetriesPerStep = 2,
            WorkspaceRoot = string.IsNullOrWhiteSpace(command.WorkingDirectory)
                ? null
                : command.WorkingDirectory,
            ParentRunId = parentRunId,
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
        };
        return await RunAsync(request, cancellationToken);
    }

    public Task CancelAsync(Guid runId)
    {
        _statusCache.TryUpdate(runId,
            new AgentRunStatus(runId, "Cancelled", 0, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            _statusCache.GetValueOrDefault(runId));
        return Task.CompletedTask;
    }

    public Task<AgentRunStatus?> GetStatusAsync(Guid runId)
    {
        _statusCache.TryGetValue(runId, out var status);
        return Task.FromResult<AgentRunStatus?>(status);
    }

    public IAsyncEnumerable<AgentRunEvent> SubscribeAsync(Guid runId, CancellationToken cancellationToken = default)
        => _eventPublisher.Subscribe(runId, cancellationToken);

    internal static string BuildResumeContext(AgentRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[resumed_task]");
        sb.AppendLine("This task was interrupted and is being resumed. Continue from where it stopped using the plan and the last observation below. Do not restart from scratch.");
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
            sb.AppendLine($"Last step: {lastStep.Step}.{lastStep.Attempt} {lastStep.Objective} (success: {lastStep.Success})");
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
}