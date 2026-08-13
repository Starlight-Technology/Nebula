using Nebula.Core.Operations;
using Nebula.Core.Safety;

namespace Nebula.Core.Agent;

public sealed record AgentRun(
    Guid Id,
    Guid ConversationId,
    Guid RequestId,
    string Prompt,
    string ModelName,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Response,
    bool IsCancelled,
    IReadOnlyList<AgentStepRecord> Steps,
    string? CurrentPlan = null,
    IReadOnlyList<AgentArtifactRecord> Artifacts = null!,
    IReadOnlyList<AgentApprovalRecord> Approvals = null!,
    string? WorkspaceRoot = null);

public sealed record AgentStepRecord(
    Guid Id,
    Guid RunId,
    int Step,
    int Attempt,
    OperationKind OperationKind,
    string Objective,
    string? Command,
    string? WorkingDirectory,
    string? TargetPath,
    int? ExitCode,
    bool Success,
    DateTimeOffset CreatedAt,
    string? StandardOutput,
    string? StandardError,
    string? Shell = null,
    CommandSafetyDecisionType? SafetyDecision = null,
    bool ApprovedByUser = false,
    bool AutoApproved = false);

public sealed record AgentArtifactRecord(
    Guid Id,
    Guid RunId,
    string Name,
    string? Path,
    string? ContentHash,
    DateTimeOffset CreatedAt);

public sealed record AgentApprovalRecord(
    Guid Id,
    Guid RunId,
    Guid StepId,
    string Objective,
    string? Command,
    CommandSafetyDecisionType Decision,
    bool ApprovedByUser,
    bool AutoApproved,
    DateTimeOffset CreatedAt);

public interface IAgentRunStore
{
    Task SaveRunAsync(
        AgentRun run,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRun>> GetRunsAsync(
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<AgentRun?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRun>> GetUnfinishedRunsAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
