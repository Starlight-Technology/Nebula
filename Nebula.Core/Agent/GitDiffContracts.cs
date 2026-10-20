namespace Nebula.Core.Agent;

public interface IGitDiffService
{
    Task<GitDiffResult> GetWorkingTreeDiffAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
