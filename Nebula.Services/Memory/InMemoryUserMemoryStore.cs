using Nebula.Core.Memory;

namespace Nebula.Services.Memory;

public sealed class InMemoryUserMemoryStore : IUserMemoryStore
{
    private readonly List<UserMemoryEntry> entries = [];
    private readonly object sync = new();

    public Task SaveAsync(
        UserMemoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var existing = entries.FirstOrDefault(value =>
                value.UserId.Equals(entry.UserId, StringComparison.OrdinalIgnoreCase) &&
                value.Kind == entry.Kind &&
                value.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                entries.Remove(existing);
            }

            entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserMemoryEntry>> GetRecentAsync(
        string userId,
        int limit = 40,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            return Task.FromResult<IReadOnlyList<UserMemoryEntry>>(entries
                .Where(entry => entry.UserId.Equals(
                    userId,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.UpdatedAt)
                .Take(Math.Max(1, limit))
                .ToList());
        }
    }
}