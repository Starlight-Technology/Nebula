namespace Nebula.Core.Agent;

public interface IGitService
{
    Task<GitStatusResult> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<GitBranchResult> GetBranchesAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<GitCommitResult> CommitAsync(string workingDirectory, string message, bool amend = false, CancellationToken cancellationToken = default);
    Task<GitCommitResult> CommitAllAsync(string workingDirectory, string message, CancellationToken cancellationToken = default);
    Task<GitRevertResult> RevertAsync(string workingDirectory, string commitHash, CancellationToken cancellationToken = default);
    Task<GitDiffResult> GetDiffAsync(string workingDirectory, bool staged = false, CancellationToken cancellationToken = default);
    Task<GitLogResult> GetLogAsync(string workingDirectory, int count = 10, CancellationToken cancellationToken = default);
    Task<GitRemoteResult> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<string> GenerateCommitMessageAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<bool> IsProtectedBranchAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<string> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default);
}

public static class GitProtectedBranches
{
    public static readonly HashSet<string> DefaultProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "master", "develop", "release", "production", "prod"
    };

    public static bool IsProtected(string branchName, HashSet<string>? customProtected = null)
    {
        var protectedBranches = customProtected ?? DefaultProtected;
        return protectedBranches.Contains(branchName);
    }
}

public sealed record GitStatusResult(
    bool IsRepository,
    string CurrentBranch,
    bool HasUncommittedChanges,
    IReadOnlyList<string> StagedFiles,
    IReadOnlyList<string> UnstagedFiles,
    IReadOnlyList<string> UntrackedFiles,
    string? Error)
{
    public static GitStatusResult NotRepository(string? error = null) =>
        new(false, "", false, [], [], [], error);
}

public sealed record GitBranchResult(
    bool Success,
    string? CurrentBranch,
    IReadOnlyList<string> AllBranches,
    string? Error);

public sealed record GitCommitResult(
    bool Success,
    string? CommitHash,
    string? Error);

public sealed record GitRevertResult(
    bool Success,
    string? RevertedCommitHash,
    string? Error);

public sealed record GitDiffResult(
    bool IsRepository,
    IReadOnlyList<string> ChangedFiles,
    string? DiffStat,
    string? Error)
{
    public static GitDiffResult NotRepository(string? error = null) =>
        new(false, [], null, error);
}

public sealed record GitLogResult(
    bool Success,
    IReadOnlyList<GitLogEntry> Entries,
    string? Error);

public sealed record GitLogEntry(
    string Hash,
    string Author,
    string Date,
    string Message);

public sealed record GitRemoteResult(
    bool Success,
    IReadOnlyList<GitRemote> Remotes,
    string? Error);

public sealed record GitRemote(
    string Name,
    string Url);
