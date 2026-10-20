using System.Net.Http.Json;
using Nebula.Core.Agent;
using Nebula.Runner;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Nebula.Agent.Infrastructure;

public sealed class PullRequestService(
    IShellExecutor executor,
    ILogger logger,
    IHttpClientFactory httpClientFactory) : IPullRequestService
{
    public async Task<PullRequestResult> CreatePullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
        {
            return new PullRequestResult(false, null, null, "Working directory does not exist.");
        }

        var repoInfo = await GetRepositoryInfoAsync(request.WorkingDirectory, cancellationToken);
        if (!repoInfo.IsRepository)
        {
            return new PullRequestResult(false, null, null, "Not a git repository.");
        }

        var provider = request.Provider;
        if (provider == PullRequestProvider.Auto)
        {
            provider = DetectProvider(repoInfo.RemoteUrl);
        }

        var hasChanges = await HasUncommittedChangesAsync(request.WorkingDirectory, cancellationToken);
        if (hasChanges)
        {
            return new PullRequestResult(false, null, null, "There are uncommitted changes. Commit or stash them first.");
        }

        var isPushed = await IsBranchPushedAsync(request.WorkingDirectory, request.SourceBranch, cancellationToken);
        if (!isPushed)
        {
            var pushResult = await PushBranchAsync(request.WorkingDirectory, request.SourceBranch, cancellationToken);
            if (!pushResult)
            {
                return new PullRequestResult(false, null, null, $"Failed to push branch '{request.SourceBranch}' to remote.");
            }
        }

        return provider switch
        {
            PullRequestProvider.GitHub => await CreateGitHubPullRequestAsync(request, repoInfo, cancellationToken),
            PullRequestProvider.GitLab => await CreateGitLabPullRequestAsync(request, repoInfo, cancellationToken),
            _ => new PullRequestResult(false, null, null, "Unsupported provider. Use GitHub or GitLab.")
        };
    }

    public async Task<GitRepositoryInfo> GetRepositoryInfoAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return new GitRepositoryInfo(false, null, null, null, null);
        }

        try
        {
            var remoteUrlResult = await RunGitAsync(workingDirectory, "git remote get-url origin", cancellationToken);
            if (!remoteUrlResult.Success || string.IsNullOrWhiteSpace(remoteUrlResult.StandardOutput))
            {
                return new GitRepositoryInfo(false, null, null, null, null);
            }

            var remoteUrl = remoteUrlResult.StandardOutput.Trim();
            var (owner, name) = ParseRepositoryInfo(remoteUrl);
            var provider = DetectProvider(remoteUrl);

            return new GitRepositoryInfo(true, remoteUrl, owner, name, provider.ToString());
        }
        catch
        {
            return new GitRepositoryInfo(false, null, null, null, null);
        }
    }

    public async Task<bool> HasUncommittedChangesAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return false;
        }

        try
        {
            var result = await RunGitAsync(workingDirectory, "git status --porcelain", cancellationToken);
            return result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsBranchPushedAsync(string workingDirectory, string branchName, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunGitAsync(workingDirectory, $"git rev-parse --verify origin/{branchName}", cancellationToken);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    private async Task<PullRequestResult> CreateGitHubPullRequestAsync(PullRequestRequest request, GitRepositoryInfo repoInfo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.GitHubToken))
        {
            return new PullRequestResult(false, null, null, "GitHub token is required for GitHub PR creation.");
        }

        if (string.IsNullOrWhiteSpace(repoInfo.Owner) || string.IsNullOrWhiteSpace(repoInfo.Name))
        {
            return new PullRequestResult(false, null, null, "Could not determine repository owner/name from remote URL.");
        }

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", request.GitHubToken);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Nebula-Agent");

            var prBody = new
            {
                title = request.Title,
                body = request.Description,
                head = request.SourceBranch,
                @base = request.TargetBranch
            };

            var url = $"https://api.github.com/repos/{repoInfo.Owner}/{repoInfo.Name}/pulls";
            var response = await httpClient.PostAsJsonAsync(url, prBody, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                return new PullRequestResult(false, null, null, $"GitHub API error: {response.StatusCode} - {errorContent}");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var prUrl = json.GetProperty("html_url").GetString();
            var prNumber = json.GetProperty("number").GetInt32();

            logger.Log($"[PR] Created GitHub PR #{prNumber}: {prUrl}");
            return new PullRequestResult(true, prUrl, prNumber, null);
        }
        catch (Exception ex)
        {
            logger.Log($"[PR] GitHub PR creation failed: {ex.Message}");
            return new PullRequestResult(false, null, null, ex.Message);
        }
    }

    private async Task<PullRequestResult> CreateGitLabPullRequestAsync(PullRequestRequest request, GitRepositoryInfo repoInfo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.GitLabToken))
        {
            return new PullRequestResult(false, null, null, "GitLab token is required for GitLab PR creation.");
        }

        if (string.IsNullOrWhiteSpace(request.GitLabProjectId) && string.IsNullOrWhiteSpace(repoInfo.Name))
        {
            return new PullRequestResult(false, null, null, "GitLab project ID is required (or repository name must be parseable from remote).");
        }

        var projectId = request.GitLabProjectId ?? $"{repoInfo.Owner}%2F{repoInfo.Name}";

        try
        {
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", request.GitLabToken);

            var mrBody = new
            {
                title = request.Title,
                description = request.Description,
                source_branch = request.SourceBranch,
                target_branch = request.TargetBranch
            };

            var url = $"https://gitlab.com/api/v4/projects/{projectId}/merge_requests";
            var response = await httpClient.PostAsJsonAsync(url, mrBody, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                return new PullRequestResult(false, null, null, $"GitLab API error: {response.StatusCode} - {errorContent}");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var mrUrl = json.GetProperty("web_url").GetString();
            var mrNumber = json.GetProperty("iid").GetInt32();

            logger.Log($"[PR] Created GitLab MR !{mrNumber}: {mrUrl}");
            return new PullRequestResult(true, mrUrl, mrNumber, null);
        }
        catch (Exception ex)
        {
            logger.Log($"[PR] GitLab MR creation failed: {ex.Message}");
            return new PullRequestResult(false, null, null, ex.Message);
        }
    }

    private static PullRequestProvider DetectProvider(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return PullRequestProvider.GitHub;

        if (remoteUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            return PullRequestProvider.GitHub;

        if (remoteUrl.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase))
            return PullRequestProvider.GitLab;

        return PullRequestProvider.GitHub; // Default
    }

    private static (string? Owner, string? Name) ParseRepositoryInfo(string remoteUrl)
    {
        try
        {
            // Handle SSH: git@github.com:owner/repo.git
            if (remoteUrl.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                var parts = remoteUrl.Split(':', 2);
                if (parts.Length == 2)
                {
                    var path = parts[1].TrimEnd('.', 'g', 'i', 't');
                    var pathParts = path.Split('/', 2);
                    if (pathParts.Length == 2)
                        return (pathParts[0], pathParts[1]);
                }
            }

            // Handle HTTPS: https://github.com/owner/repo.git
            if (remoteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(remoteUrl);
                var path = uri.AbsolutePath.Trim('/').TrimEnd(".git".ToCharArray());
                var pathParts = path.Split('/', 2);
                if (pathParts.Length == 2)
                    return (pathParts[0], pathParts[1]);
            }

            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<bool> PushBranchAsync(string workingDirectory, string branchName, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(workingDirectory, $"git push origin {branchName}", cancellationToken);
        return result.Success;
    }

    private async Task<ShellCommandResult> RunGitAsync(string workingDirectory, string command, CancellationToken cancellationToken)
    {
        try
        {
            if (executor is IDetailedShellExecutor detailedExecutor)
            {
                return await detailedExecutor.RunCommandDetailedAsync(command, workingDirectory, cancellationToken);
            }

            var output = await executor.RunCommandAsync(command, cancellationToken);
            return new ShellCommandResult
            {
                Command = command,
                WorkingDirectory = workingDirectory,
                StandardOutput = output,
                ExitCode = 0,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new ShellCommandResult
            {
                Command = command,
                WorkingDirectory = workingDirectory,
                StandardOutput = string.Empty,
                StandardError = ex.Message,
                ExitCode = -1,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
    }
}