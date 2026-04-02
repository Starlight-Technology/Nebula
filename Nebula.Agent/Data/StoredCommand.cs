namespace Nebula.Agent.Data;

/// <summary>
/// Represents a validated command that was executed or is pending execution.
/// Only commands that pass safety and correctness verification are persisted.
/// Stored in PostgreSQL for reliable transaction management and querying.
/// </summary>
public class StoredCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public long? CommandId { get; set; } // Nullable for compatibility with LLM response
    public string Objective { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string OsType { get; set; } = string.Empty; // "Windows", "Linux", "macOS"
    public bool Executed { get; set; } = false;
    public string? ExecutionResult { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
