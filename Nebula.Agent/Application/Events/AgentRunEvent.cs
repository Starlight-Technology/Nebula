using Nebula.Agent.Domain;
using Nebula.Core.Agent;
using Nebula.Core.Commands;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Projects;
using Nebula.Core.Safety;

namespace Nebula.Agent.Application.Events;

public abstract record AgentRunEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt
);

public sealed record AgentRunStartedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    string Prompt,
    InteractionMode Mode,
    string WorkspaceRoot
) : AgentRunEvent(RunId, Timestamp, 0, 0);

public sealed record AgentRunCompletedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    ConversationTurn FinalTurn
) : AgentRunEvent(RunId, Timestamp, 0, 0);

public sealed record AgentRunFailedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    string ErrorMessage,
    ConversationTurn? PartialTurn
) : AgentRunEvent(RunId, Timestamp, 0, 0);

public sealed record AgentRunCancelledEvent(
    Guid RunId,
    DateTimeOffset Timestamp
) : AgentRunEvent(RunId, Timestamp, 0, 0);

public sealed record AgentRunStepStartedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    string Objective,
    OperationKind OperationKind,
    string? Command
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunStepCompletedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    string Observation,
    bool Success
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunStepRetryEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    string FailureReason,
    int RetryNumber,
    int MaxRetries
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunCommandExecutingEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    CommandExecution Command
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunCommandOutputEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    string Chunk,
    bool IsError
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunCommandCompletedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    CommandExecution Command,
    int ExitCode,
    bool Success
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunApprovalRequiredEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    CommandExecution Command,
    string Reason
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunApprovalGrantedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    CommandExecution Command,
    bool AutoApproved
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunPlanUpdatedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    IReadOnlyList<AgentPlanStep> Plan
) : AgentRunEvent(RunId, Timestamp, 0, 0);

public sealed record AgentRunPlanStepCompletedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int StepId,
    string Description
) : AgentRunEvent(RunId, Timestamp, 0, 0);

public sealed record AgentRunSafetyDecisionEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    CommandClassification Classification,
    CommandSafetyDecision Decision
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunDryRunPreviewEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    IReadOnlyList<CommandExecution> PreviewCommands
) : AgentRunEvent(RunId, Timestamp, 0, 0);

public sealed record AgentRunErrorReflectionEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    string Hypothesis,
    string AlternativeAction,
    string NextCommand
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);

public sealed record AgentRunPlanRevisedEvent(
    Guid RunId,
    DateTimeOffset Timestamp,
    int Step,
    int Attempt,
    string FailedStep,
    string AlternativeStep
) : AgentRunEvent(RunId, Timestamp, Step, Attempt);