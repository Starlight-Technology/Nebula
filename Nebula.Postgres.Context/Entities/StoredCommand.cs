using System;

namespace Nebula.Postgres.Context.Entities;

public class StoredCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RequestId { get; set; }

    public long? CommandId { get; set; }

    public string Objective { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string OsType { get; set; } = string.Empty;

    public bool Executed { get; set; } = false;

    public string? ExecutionResult { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Request? Request { get; set; }
}