using Nebula.Core.Memory;

namespace Nebula.Services.Memory;

public sealed class InMemoryWorkspaceMemoryStore : IWorkspaceMemoryStore
{
    private const int MaxEntries = 2000;

    private readonly List<WorkspaceMemoryEntry> entries = [];
    private readonly object sync = new();

    public Task SaveAsync(
        WorkspaceMemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var index = entries.FindIndex(value => value.Matches(entry));
            if (index < 0)
            {
                entries.Add(entry);
            }
            else
            {
                // Upsert por {workspace, kind, key} (mesma semantica do
                // PostgresWorkspaceMemoryStore): mantem o Id original e
                // atualiza valor, evidencia e data.
                var existing = entries[index];
                entries[index] = existing with
                {
                    Value = entry.Value,
                    Evidence = entry.Evidence,
                    CreatedAt = entry.CreatedAt
                };
            }

            // Teto anti-crescimento ilimitado: remove os mais antigos.
            while (entries.Count > MaxEntries)
            {
                var oldest = 0;
                for (var i = 1; i < entries.Count; i++)
                {
                    if (entries[i].CreatedAt < entries[oldest].CreatedAt)
                    {
                        oldest = i;
                    }
                }

                entries.RemoveAt(oldest);
            }
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