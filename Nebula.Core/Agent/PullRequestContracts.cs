namespace Nebula.Core.Agent;

public sealed record PullRequestRequest(
    string WorkingDirectory,
    string Title,
    string Description,
    string SourceBranch,
    string TargetBranch,
    PullRequestProvider Provider,
    string? GitHubToken = null,
    string? GitLabToken = null,
    string? GitLabProjectId = null);

public sealed record PullRequestResult(
    bool Success,
    string? PullRequestUrl,
    int? PullRequestNumber,
    string? Error);

public enum PullRequestProvider
{
    GitHub,
    GitLab,
    Auto
}

public sealed record GitRepositoryInfo(
    bool IsRepository,
    string? RemoteUrl,
    string? Owner,
    string? Name,
    string? Provider);

public interface IPullRequestService
{
    Task<PullRequestResult> CreatePullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken = default);
    Task<GitRepositoryInfo> GetRepositoryInfoAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<bool> IsBranchPushedAsync(string workingDirectory, string branchName, CancellationToken cancellationToken = default);
}