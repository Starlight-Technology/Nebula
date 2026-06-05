namespace Nebula.Agent;

public class ConversationTurn
{
    public Guid ConversationId { get; set; }

    public Guid RequestId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;

    public string Response { get; set; } = string.Empty;

    public string? Reasoning { get; set; }

    public List<CommandExecution> Commands { get; set; } = [];

    public ActionExecutionStatus? ActionStatus { get; set; }

    public List<ActionExecutionEvent> ActionEvents { get; set; } = [];

    public bool IsCancelled { get; set; }
}

public class CommandExecution
{
    public int Attempt { get; set; } = 1;

    public int Id { get; set; }

    public string Objective { get; set; } = string.Empty;

    public string Run { get; set; } = string.Empty;

    public bool Required { get; set; } = true;

    public bool IsCorrect { get; set; }

    public bool IsSafe { get; set; }

    public bool PassedLocalSafety { get; set; }

    public bool Executed { get; set; }

    public bool Skipped { get; set; }

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
    Cancelled
}

public class ActionExecutionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ActionExecutionStatus Status { get; set; }

    public int Attempt { get; set; } = 1;

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Command { get; set; }

    public string? ToolResponse { get; set; }

    public string? Error { get; set; }
}

public class ActionValidationResult
{
    public bool Safe { get; set; }

    public bool Allowed { get; set; }

    public bool Feasible { get; set; }

    public string Reason { get; set; } = string.Empty;

    public bool IsValid => Safe && Allowed && Feasible;
}
