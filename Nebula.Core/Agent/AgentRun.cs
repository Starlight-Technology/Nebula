using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Core.Interactions;

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
    string? WorkspaceRoot = null,
    InteractionMode Mode = InteractionMode.Agent,
    IReadOnlyList<ArchitectureComparisonOption> ArchitectureOptions = null!,
    Guid? ParentRunId = null);

public sealed record ArchitectureComparisonOption(
    string Name,
    string[] Pros,
    string[] Cons,
    string Recommendation,
    string Risk);

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
    DateTimeOffset CreatedAt,
    double DiffPercent = 0.0);

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

    Task<Guid?> GetRunIdByStepIdAsync(
        Guid stepId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRun>> GetRunsByConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
