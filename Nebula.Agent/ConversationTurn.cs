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
}

public class CommandExecution
{
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
}
