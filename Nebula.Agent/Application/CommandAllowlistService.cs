using Nebula.Core.Memory;

namespace Nebula.Agent.Application;

public sealed class CommandAllowlistService(
    IWorkspaceMemoryStore store,
    ILogger logger) : ICommandAllowlistService
{
    public async Task<bool> IsAllowedAsync(
        string workspace,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) ||
            string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        try
        {
            return await store.ExistsAsync(
                workspace,
                WorkspaceMemoryKind.AllowlistedCommand,
                CommandNormalization.Normalize(command),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[WORKSPACE-ALLOWLIST] Check failed (non-fatal): {ex.Message}");
            return false;
        }
    }

    public async Task AddAsync(
        string workspace,
        string command,
        string? evidence = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) ||
            string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var normalized = CommandNormalization.Normalize(command);
        try
        {
            var exists = await store.ExistsAsync(
                workspace,
                WorkspaceMemoryKind.AllowlistedCommand,
                normalized,
                cancellationToken);
            if (exists)
            {
                return;
            }

            await store.SaveAsync(
                new WorkspaceMemoryEntry(
                    Guid.NewGuid(),
                    workspace,
                    WorkspaceMemoryKind.AllowlistedCommand,
                    normalized,
                    command.Trim(),
                    string.IsNullOrWhiteSpace(evidence)
                        ? "Added by user."
                        : evidence,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[WORKSPACE-ALLOWLIST] Add failed (non-fatal): {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<WorkspaceMemoryEntry>> ListAsync(
        string workspace,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return [];
        }

        try
        {
            var entries = await store.GetRecentAsync(
                workspace,
                limit: 100,
                cancellationToken);
            return entries
                .Where(entry =>
                    entry.Kind == WorkspaceMemoryKind.AllowlistedCommand)
                .OrderBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[WORKSPACE-ALLOWLIST] List failed (non-fatal): {ex.Message}");
            return [];
        }
    }
}
