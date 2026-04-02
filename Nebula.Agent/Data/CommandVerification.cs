namespace Nebula.Agent.Data;

/// <summary>
/// Represents verification results for a command (correctness and safety checks).
/// Provides audit trail of verification decisions.
/// </summary>
public class CommandVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommandId { get; set; }
    public bool IsCorrect { get; set; }
    public bool IsSafe { get; set; }
    public string? VerificationNotes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
