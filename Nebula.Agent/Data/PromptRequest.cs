namespace Nebula.Agent.Data;

/// <summary>
/// Represents a user request/prompt that was processed by the system.
/// Tracked in MongoDB for audit and history purposes.
/// </summary>
public class PromptRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Prompt { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty; // "Action", "Chat", "Unknown"
    public string? Response { get; set; } = null;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
