namespace Nebula.Core.Memory;

public enum UserMemoryKind
{
    Language,
    Style,
    DetailLevel,
    AutonomyTolerance
}

public sealed record UserMemoryEntry(
    Guid Id,
    string UserId,
    UserMemoryKind Kind,
    string Key,
    string Value,
    DateTimeOffset UpdatedAt);

public interface IUserMemoryStore
{
    Task SaveAsync(
        UserMemoryEntry entry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserMemoryEntry>> GetRecentAsync(
        string userId,
        int limit = 40,
        CancellationToken cancellationToken = default);
}
