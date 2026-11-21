using System.Collections.Generic;
using Nebula.Agent.Application.Events;
using Nebula.Agent.Domain;
using Nebula.Core.Agent;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Safety;

namespace Nebula.Agent.Application;

public interface IAgentRunOrchestrator
{
    Task<Guid> RunAsync(AgentActionRunRequest request, CancellationToken cancellationToken = default);
    Task<Guid> RunDryRunAsync(AgentActionRunRequest request, CancellationToken cancellationToken = default);
    Task<Guid> ResumeAsync(AgentRun run, CancellationToken cancellationToken = default);
    Task<Guid> RunApprovedCommandAsync(
        CommandExecution command,
        ApprovalScope scope,
        Guid conversationId,
        Guid requestId,
        CancellationToken cancellationToken = default);
    Task CancelAsync(Guid runId);
    Task<AgentRunStatus?> GetStatusAsync(Guid runId);
    IAsyncEnumerable<AgentRunEvent> SubscribeAsync(Guid runId, CancellationToken cancellationToken = default);
}

public sealed record AgentRunStatus(
    Guid RunId,
    string Status,
    int CurrentStep,
    int MaxSteps,
    string? CurrentObjective,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt
);

public enum AgentRunOrchestratorStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    AwaitingApproval
}