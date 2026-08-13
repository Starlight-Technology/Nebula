using System;

namespace Nebula.Postgres.Context.Entities;

public class WorkspaceMemoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Workspace { get; set; } = string.Empty;

    public int Kind { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string? Evidence { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}