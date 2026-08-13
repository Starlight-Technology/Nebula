namespace Nebula.Core.Memory;

/// <summary>
/// Per-workspace allowlist of frequent, previously approved commands
/// (build, test, format, lint, docker compose, migrations, local scripts).
/// Commands on the allowlist skip the approval prompt for that workspace.
/// </summary>
public interface ICommandAllowlistService
{
    Task<bool> IsAllowedAsync(
        string workspace,
        string command,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        string workspace,
        string command,
        string? evidence = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceMemoryEntry>> ListAsync(
        string workspace,
        CancellationToken cancellationToken = default);
}
