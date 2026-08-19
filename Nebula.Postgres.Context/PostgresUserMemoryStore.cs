using Microsoft.EntityFrameworkCore;

using Nebula.Core.Memory;

using UserMemoryEntryEntity =
    Nebula.Postgres.Context.Entities.UserMemoryEntry;

namespace Nebula.Postgres.Context;

public sealed class PostgresUserMemoryStore(PostgresContext context)
    : IUserMemoryStore
{
    public async Task SaveAsync(
        UserMemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        var entity = Map(entry);
        var existing = await context.UserMemoryEntries
            .SingleOrDefaultAsync(
                value => value.UserId == entry.UserId &&
                         value.Kind == (int)entry.Kind &&
                         value.Key == entry.Key,
                cancellationToken);
        if (existing is null)
        {
            context.UserMemoryEntries.Add(entity);
        }
        else
        {
            existing.Value = entry.Value;
            existing.UpdatedAt = entry.UpdatedAt;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserMemoryEntry>> GetRecentAsync(
        string userId,
        int limit = 40,
        CancellationToken cancellationToken = default)
    {
        var entities = await context.UserMemoryEntries
            .AsNoTracking()
            .Where(entry => entry.UserId == userId)
            .OrderByDescending(entry => entry.UpdatedAt)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToList();
    }

    private static UserMemoryEntryEntity Map(
        UserMemoryEntry entry) =>
        new()
        {
            Id = entry.Id,
            UserId = entry.UserId,
            Kind = (int)entry.Kind,
            Key = entry.Key,
            Value = entry.Value,
            UpdatedAt = entry.UpdatedAt
        };

    private static UserMemoryEntry Map(
        UserMemoryEntryEntity entity) =>
        new(
            entity.Id,
            entity.UserId,
            (UserMemoryKind)entity.Kind,
            entity.Key,
            entity.Value,
            entity.UpdatedAt);
}