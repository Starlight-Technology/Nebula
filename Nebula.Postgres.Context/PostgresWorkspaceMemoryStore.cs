using Microsoft.EntityFrameworkCore;

using Nebula.Core.Memory;

using WorkspaceMemoryEntryEntity =
    Nebula.Postgres.Context.Entities.WorkspaceMemoryEntry;

namespace Nebula.Postgres.Context;

public sealed class PostgresWorkspaceMemoryStore(PostgresContext context)
    : IWorkspaceMemoryStore
{
    public async Task SaveAsync(
        WorkspaceMemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        var entity = Map(entry);
        var existing = await context.WorkspaceMemoryEntries
            .SingleOrDefaultAsync(
                value => value.Workspace == entry.Workspace &&
                         value.Kind == (int)entry.Kind &&
                         value.Key == entry.Key,
                cancellationToken);
        if (existing is null)
        {
            context.WorkspaceMemoryEntries.Add(entity);
        }
        else
        {
            existing.Value = entry.Value;
            existing.Evidence = entry.Evidence;
            existing.CreatedAt = entry.CreatedAt;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkspaceMemoryEntry>> GetRecentAsync(
        string workspace,
        int limit = 40,
        CancellationToken cancellationToken = default)
    {
        var entities = await context.WorkspaceMemoryEntries
            .AsNoTracking()
            .Where(entry => entry.Workspace == workspace)
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToList();
    }

    public async Task<bool> ExistsAsync(
        string workspace,
        WorkspaceMemoryKind kind,
        string key,
        CancellationToken cancellationToken = default)
    {
        return await context.WorkspaceMemoryEntries
            .AsNoTracking()
            .AnyAsync(
                entry =>
                    entry.Workspace == workspace &&
                    entry.Kind == (int)kind &&
                    entry.Key == key,
                cancellationToken);
    }

    private static WorkspaceMemoryEntryEntity Map(
        WorkspaceMemoryEntry entry) =>
        new()
        {
            Id = entry.Id,
            Workspace = entry.Workspace,
            Kind = (int)entry.Kind,
            Key = entry.Key,
            Value = entry.Value,
            Evidence = entry.Evidence,
            CreatedAt = entry.CreatedAt
        };

    private static WorkspaceMemoryEntry Map(
        WorkspaceMemoryEntryEntity entity) =>
        new(
            entity.Id,
            entity.Workspace,
            (WorkspaceMemoryKind)entity.Kind,
            entity.Key,
            entity.Value,
            entity.Evidence,
            entity.CreatedAt);
}