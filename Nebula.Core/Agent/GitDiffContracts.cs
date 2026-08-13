namespace Nebula.Core.Agent;

public sealed record GitDiffResult(
    bool IsRepository,
    IReadOnlyList<string> ChangedFiles,
    string? DiffStat,
    string? Error)
{
    public static GitDiffResult NotRepository(string? error = null) =>
        new(false, [], null, error);
}

public interface IGitDiffService
{
    Task<GitDiffResult> GetWorkingTreeDiffAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
