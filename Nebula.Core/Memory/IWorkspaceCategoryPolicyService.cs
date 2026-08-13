namespace Nebula.Core.Memory;

/// <summary>
/// Per-workspace auto-approve categories (e.g. "package-install",
/// "network-access"). Categories configured for a workspace combine with
/// the global <c>Nebula:AutoApproveCategories</c> list when the approval
/// override is evaluated, but they are scoped to that workspace only.
/// </summary>
public interface IWorkspaceCategoryPolicyService
{
    Task<bool> IsAllowedAsync(
        string workspace,
        string category,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        string workspace,
        string category,
        string? evidence = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkspaceMemoryEntry>> ListAsync(
        string workspace,
        CancellationToken cancellationToken = default);
}