using System;

namespace Nebula.Postgres.Context.Entities;

public class CommandVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CommandId { get; set; }

    public bool IsCorrect { get; set; }

    public bool IsSafe { get; set; }

    public string? VerificationNotes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public StoredCommand? Command { get; set; }
}