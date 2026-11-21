using System.Text.RegularExpressions;

namespace Nebula.Core.Agent;

public static class MigrationNaming
{
    private static readonly Regex NamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled);

    private static readonly Regex ContextPattern = new(
        "^[A-Za-z_][A-Za-z0-9_.]*$",
        RegexOptions.Compiled);

    public static bool IsValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && NamePattern.IsMatch(value);

    public static bool IsValidContext(string? value) =>
        string.IsNullOrWhiteSpace(value) || ContextPattern.IsMatch(value);
}

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

public enum MigrationAction
{
    Create,
    Apply,
    List
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