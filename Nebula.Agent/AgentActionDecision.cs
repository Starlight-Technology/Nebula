namespace Nebula.Agent;

public sealed class AgentActionDecisionRequest
{
    public string Objective { get; set; } = string.Empty;

    public string ChatHistoryContext { get; set; } = string.Empty;

    public string CurrentPlan { get; set; } = string.Empty;

    public string? PreviousActionResult { get; set; }

    public IReadOnlyList<string> Observations { get; set; } = [];

    public int StepNumber { get; set; }

    public int RetryNumber { get; set; }
}

public sealed class AgentActionDecision
{
    public string ReasoningSummary { get; set; } = string.Empty;

    public bool IsComplete { get; set; }

    public string CompletionMessage { get; set; } = string.Empty;

    public AgentToolAction? Action { get; set; }
}

public sealed class AgentToolAction
{
    public string Objective { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public bool RequiresSafetyReview { get; set; } = true;
}
