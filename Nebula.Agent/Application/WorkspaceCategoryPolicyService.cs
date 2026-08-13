using Nebula.Core.Memory;

namespace Nebula.Agent.Application;

public sealed class WorkspaceCategoryPolicyService(
    IWorkspaceMemoryStore store,
    ILogger logger) : IWorkspaceCategoryPolicyService
{
    public async Task<bool> IsAllowedAsync(
        string workspace,
        string category,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) ||
            string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        try
        {
            return await store.ExistsAsync(
                workspace,
                WorkspaceMemoryKind.AutoApprovedCategory,
                NormalizeCategory(category),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[WORKSPACE-CATEGORY-POLICY] Check failed (non-fatal): {ex.Message}");
            return false;
        }
    }

    public async Task AddAsync(
        string workspace,
        string category,
        string? evidence = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) ||
            string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        var normalized = NormalizeCategory(category);
        try
        {
            var exists = await store.ExistsAsync(
                workspace,
                WorkspaceMemoryKind.AutoApprovedCategory,
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
                    WorkspaceMemoryKind.AutoApprovedCategory,
                    normalized,
                    category.Trim(),
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
            logger.Log($"[WORKSPACE-CATEGORY-POLICY] Add failed (non-fatal): {ex.Message}");
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
                    entry.Kind == WorkspaceMemoryKind.AutoApprovedCategory)
                .OrderBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[WORKSPACE-CATEGORY-POLICY] List failed (non-fatal): {ex.Message}");
            return [];
        }
    }

    private static string NormalizeCategory(string category) =>
        CommandNormalization.Normalize(category);
}