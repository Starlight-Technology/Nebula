using Nebula.Agent.Domain;
using Nebula.Core.Operations;
using Nebula.Core.Projects;

namespace Nebula.Agent;

public sealed class AgentActionDecisionRequest
{
    public string Objective { get; set; } = string.Empty;

    public string ChatHistoryContext { get; set; } = string.Empty;

    public string CurrentPlan { get; set; } = string.Empty;

    public string? PreviousActionResult { get; set; }

    public IReadOnlyList<string> Observations { get; set; } = [];

    public IReadOnlyList<ExecutionHistoryEntry> ExecutionHistory { get; set; } = [];

    public int StepNumber { get; set; }

    public int RetryNumber { get; set; }

    /// <summary>
    /// Optional reference workspace root this decision is made for.
    /// When null, the runner falls back to the resolved default workspace.
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>
    /// True when this decision is a dry-run preview: nothing may be executed or written.
    /// </summary>
    public bool DryRun { get; set; }
}

public sealed class AgentActionDecision
{
    public string ReasoningSummary { get; set; } = string.Empty;

    public bool IsComplete { get; set; }

    public string CompletionMessage { get; set; } = string.Empty;

    public AgentToolAction? Action { get; set; }

    public IReadOnlyList<AgentPlanStep>? Plan { get; set; }

    /// <summary>
    /// Optional comparison of architecture options considered before a large
    /// change. Emitted before implementation so the human can review the trade-offs.
    /// </summary>
    public IReadOnlyList<ArchitectureOption>? ArchitectureComparison { get; set; }
}

public sealed class ArchitectureOption
{
    public string Name { get; set; } = string.Empty;

    public string Pros { get; set; } = string.Empty;

    public string Cons { get; set; } = string.Empty;

    public string Recommendation { get; set; } = string.Empty;

    public string Risk { get; set; } = "medium";
}

public sealed class AgentPlanStep
{
    public int Id { get; set; }

    public string Description { get; set; } = string.Empty;

    public IReadOnlyList<int> DependsOn { get; set; } = [];

    public string Status { get; set; } = "pending";

    /// <summary>
    /// Estimated risk of the step: low, medium, high or critical.
    /// </summary>
    public string Risk { get; set; } = "low";

    /// <summary>
    /// Milestones (verification gates such as build/test) of a large task.
    /// </summary>
    public bool IsCheckpoint { get; set; }
}

public sealed class AgentToolAction
{
    public string Objective { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public OperationKind OperationKind { get; set; } = OperationKind.Unknown;

    public string? Content { get; set; }

    public string? TargetPath { get; set; }

    public string? TemplateId { get; set; }

    public IReadOnlyList<PlannedPatchFile>? PlannedFiles { get; set; }

    public string? Language { get; set; }

    public string? WorkingDirectory { get; set; }

    public string? RetryJustification { get; set; }

    public bool RequiresSafetyReview { get; set; } = true;
}

public sealed class ErrorReflection
{
    public string Hypothesis { get; set; } = string.Empty;

    public string AlternativeAction { get; set; } = string.Empty;

    public string NextCommand { get; set; } = string.Empty;
}
