using Nebula.Agent.Domain;
using Nebula.Core.Agent;
using Nebula.Core.Commands;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Projects;
using Nebula.Core.Safety;

namespace Nebula.Agent;

public class ConversationTurn
{
    public Guid ConversationId { get; set; }

    public Guid RequestId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public InteractionMode Mode { get; set; }

    public string ModelName { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;

    public string Response { get; set; } = string.Empty;

    public string? Reasoning { get; set; }

    public string? FinalReport { get; set; }

    public List<CommandExecution> Commands { get; set; } = [];

    public List<ExecutionHistoryEntry> ExecutionHistory { get; set; } = [];

    public List<ExecutionEvidence> Evidence { get; set; } = [];

    public ActionExecutionStatus? ActionStatus { get; set; }

    public List<ActionExecutionEvent> ActionEvents { get; set; } = [];

    public string CurrentPlan { get; set; } = string.Empty;

    public List<AgentArtifactRecord> Artifacts { get; set; } = [];

    public List<AgentApprovalRecord> Approvals { get; set; } = [];

    public bool IsCancelled { get; set; }
}

public class CommandExecution
{
    public Guid StepId { get; set; } = Guid.NewGuid();

    public OperationKind OperationKind { get; set; } =
        OperationKind.TerminalCommand;

    public int Attempt { get; set; } = 1;

    public int Id { get; set; }

    public string Objective { get; set; } = string.Empty;

    public string Run { get; set; } = string.Empty;

    public string OriginalCommand { get; set; } = string.Empty;

    public string ResolvedFileName { get; set; } = string.Empty;

    public string ResolvedArguments { get; set; } = string.Empty;

    public OperatingSystemKind OperatingSystem { get; set; }

    public ShellKind Shell { get; set; }

    public IReadOnlyList<string> ResolutionReasons { get; set; } = [];

    public string WorkingDirectory { get; set; } = string.Empty;

    public string? TargetPath { get; set; }

    public IReadOnlyList<PlannedPatchFile>? PlannedFiles { get; set; }

    /// <summary>
    /// File content proposed by the agent (FileWrite/ScriptContent). Needed to
    /// replay an approved action without the LLM.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// Script language of the proposed content (ScriptContent).
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Template id of the proposed scaffold (ProjectScaffold).
    /// </summary>
    public string? TemplateId { get; set; }

    public string? ContentHash { get; set; }

    public string ClassificationSource { get; set; } = string.Empty;

    public double ClassificationConfidence { get; set; }

    public CommandSafetyDecisionType? SafetyDecision { get; set; }

    public bool ApprovedByUser { get; set; }

    public bool AutoApproved { get; set; }

    public bool Sandboxed { get; set; }

    public bool Required { get; set; } = true;

    public bool IsCorrect { get; set; }

    public bool IsSafe { get; set; }

    public bool PassedLocalSafety { get; set; }

    public bool Executed { get; set; }

    public bool Skipped { get; set; }

    public string StandardOutput { get; set; } = string.Empty;

    public string StandardError { get; set; } = string.Empty;

    public int? ExitCode { get; set; }

    public DateTimeOffset? ExecutedAt { get; set; }

    public string? Output { get; set; }

    public string? Notes { get; set; }

    public string? Error { get; set; }
}

public enum ActionExecutionStatus
{
    Started,
    Validating,
    Planning,
    Executing,
    Retrying,
    Completed,
    Failed,
    Unsafe,
    AwaitingApproval,
    Cancelled,
    Observing,
    Correcting,
    Blocked
}

public enum ActionExecutionEventKind
{
    ReasoningSummary,
    ActionStarted,
    ActionCompleted,
    Observation,
    ErrorReflection,
    PlanRevised,
    DeduplicationBlocked,
    RetryScheduled,
    ApprovalGranted,
    Completed,
    Failed,
    Unsafe,
    ApprovalRequired,
    Cancelled,
    StreamOutput
}

public class ActionExecutionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ActionExecutionStatus Status { get; set; }

    public ActionExecutionEventKind Kind { get; set; }

    public int Step { get; set; } = 1;

    public int Attempt { get; set; } = 1;

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Command { get; set; }

    public string? ToolResponse { get; set; }

    public string? Error { get; set; }

    public bool IsError { get; set; }
}

public class ActionValidationResult
{
    public bool Safe { get; set; }

    public bool Allowed { get; set; }

    public bool Feasible { get; set; }

    public string Reason { get; set; } = string.Empty;

    public bool IsValid => Safe && Allowed && Feasible;
}
