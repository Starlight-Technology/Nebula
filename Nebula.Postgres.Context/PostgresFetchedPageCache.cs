using Microsoft.EntityFrameworkCore;

using Nebula.Core.Learning;

using FetchedPageCacheEntity =
    Nebula.Postgres.Context.Entities.FetchedPageCache;

namespace Nebula.Postgres.Context;

public sealed class PostgresFetchedPageCache(
    PostgresContext context) : IFetchedPageCache
{
    public async Task<FetchedPageCacheEntry?> GetAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var entry = await context.FetchedPageCaches
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value =>
                    value.Url == url &&
                    value.ExpiresAt > DateTimeOffset.UtcNow,
                cancellationToken);
        return entry is null
            ? null
            : new FetchedPageCacheEntry(
                entry.Url,
                entry.Html,
                entry.HtmlHash,
                entry.RetrievedAt,
                entry.ExpiresAt);
    }

    public async Task SetAsync(
        FetchedPageCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        var existing = await context.FetchedPageCaches
            .SingleOrDefaultAsync(
                value => value.Url == entry.Url,
                cancellationToken);
        var entity = existing ?? new FetchedPageCacheEntity();
        entity.Url = entry.Url;
        entity.Html = entry.Html;
        entity.HtmlHash = entry.HtmlHash;
        entity.RetrievedAt = entry.RetrievedAt;
        entity.ExpiresAt = entry.ExpiresAt;
        if (existing is null)
        {
            context.FetchedPageCaches.Add(entity);
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
