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

    public string? WorkingDirectory { get; set; }

    public string? Shell { get; set; }

    public int? ExitCode { get; set; }

    public string? StandardOutput { get; set; }

    public string? StandardError { get; set; }

    public string? SafetyDecision { get; set; }

    public bool ApprovedByUser { get; set; }

    public bool AutoApproved { get; set; }

    public bool Skipped { get; set; }

    public bool Required { get; set; } = true;

    public DateTimeOffset? ExecutedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Request? Request { get; set; }
}