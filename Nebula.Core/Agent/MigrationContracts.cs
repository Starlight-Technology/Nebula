namespace Nebula.Core.Agent;

public sealed record MigrationRequest(
    string WorkingDirectory,
    MigrationFramework Framework,
    string? MigrationName = null,
    string? ContextName = null,
    bool DryRun = false);

public sealed record MigrationResult(
    bool Success,
    string? MigrationFilePath,
    string? MigrationName,
    string? SqlScript,
    string? Output,
    string? Error);

public enum MigrationFramework
{
    Unknown,
    EfCore,
    NodeJs,
    Python,
    Go
}

public sealed record PendingMigrationInfo(
    string MigrationId,
    string Name,
    string CreatedAt,
    bool IsApplied);

public interface IMigrationService
{
    Task<MigrationResult> CreateMigrationAsync(MigrationRequest request, CancellationToken cancellationToken = default);
    Task<MigrationResult> ApplyMigrationsAsync(string workingDirectory, MigrationFramework framework, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingMigrationInfo>> GetPendingMigrationsAsync(string workingDirectory, MigrationFramework framework, CancellationToken cancellationToken = default);
    Task<MigrationFramework> DetectFrameworkAsync(string workingDirectory, CancellationToken cancellationToken = default);
}