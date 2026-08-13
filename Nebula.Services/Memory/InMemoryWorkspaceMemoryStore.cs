using Nebula.Core.Memory;

namespace Nebula.Services.Memory;

public sealed class InMemoryWorkspaceMemoryStore : IWorkspaceMemoryStore
{
    private readonly List<WorkspaceMemoryEntry> entries = [];
    private readonly object sync = new();

    public Task SaveAsync(
        WorkspaceMemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkspaceMemoryEntry>> GetRecentAsync(
        string workspace,
        int limit = 40,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceMemoryEntry>>(entries
                .Where(entry => entry.Workspace.Equals(
                    workspace,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(Math.Max(1, limit))
                .ToList());
        }
    }

    public Task<bool> ExistsAsync(
        string workspace,
        WorkspaceMemoryKind kind,
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            return Task.FromResult(entries.Any(entry =>
                entry.Workspace.Equals(workspace, StringComparison.OrdinalIgnoreCase) &&
                entry.Kind == kind &&
                entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));
        }
    }
}