namespace Nebula.Core.Memory;

public enum WorkspaceMemoryKind
{
    WorkingCommand,
    UsedPort,
    Script,
    Note,
    AllowlistedCommand,
    AutoApprovedCategory,
    Strategy
}

public sealed record WorkspaceMemoryEntry(
    Guid Id,
    string Workspace,
    WorkspaceMemoryKind Kind,
    string Key,
    string Value,
    string? Evidence,
    DateTimeOffset CreatedAt)
{
    public bool Matches(WorkspaceMemoryEntry other) =>
        string.Equals(Workspace, other.Workspace, StringComparison.OrdinalIgnoreCase) &&
        Kind == other.Kind &&
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
}

public interface IWorkspaceMemoryStore
{
    Task SaveAsync(
        WorkspaceMemoryEntry entry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceMemoryEntry>> GetRecentAsync(
        string workspace,
        int limit = 40,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string workspace,
        WorkspaceMemoryKind kind,
        string key,
        CancellationToken cancellationToken = default);
}