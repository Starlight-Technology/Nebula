using Nebula.Llama.Client;
using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Infrastructure;

public sealed class GitService(
    IShellExecutor executor,
    ILogger logger,
    ILlamaClient? llamaClient = null) : IGitService
{
    private readonly HashSet<string> _protectedBranches = GitProtectedBranches.DefaultProtected;

    public async Task<GitStatusResult> GetStatusAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return GitStatusResult.NotRepository("Working directory does not exist.");
        }

        try
        {
            var repoCheck = await RunAsync("git rev-parse --is-inside-work-tree", workingDirectory, cancellationToken);
            if (!repoCheck.Success)
            {
                return GitStatusResult.NotRepository();
            }

            var branchResult = await GetCurrentBranchAsync(workingDirectory, cancellationToken);
            if (string.IsNullOrWhiteSpace(branchResult))
            {
                return GitStatusResult.NotRepository("Could not determine current branch.");
            }

            var statusOutput = await RunAsync("git status --porcelain=v1", workingDirectory, cancellationToken);
            if (!statusOutput.Success)
            {
                return GitStatusResult.NotRepository(statusOutput.StandardError ?? "git status failed");
            }

            var staged = new List<string>();
            var unstaged = new List<string>();
            var untracked = new List<string>();

            foreach (var line in statusOutput.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 3) continue;
                var status = line[..2];
                var file = line[3..].Trim();

                if (status[0] != ' ' && status[0] != '?')
                    staged.Add(file);
                if (status[1] != ' ')
                    unstaged.Add(file);
                if (status == "??")
                    untracked.Add(file);
            }

            return new GitStatusResult(
                true,
                branchResult,
                staged.Count > 0 || unstaged.Count > 0 || untracked.Count > 0,
                staged,
                unstaged,
                untracked,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[GIT] Status error: {ex.Message}");
            return GitStatusResult.NotRepository(ex.Message);
        }
    }

    public async Task<GitBranchResult> GetBranchesAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return new GitBranchResult(false, null, [], "Working directory does not exist.");
        }

        try
        {
            var currentBranch = await GetCurrentBranchAsync(workingDirectory, cancellationToken);
            if (string.IsNullOrWhiteSpace(currentBranch))
            {
                return new GitBranchResult(false, null, [], "Not a git repository.");
            }

            var branchesOutput = await RunAsync("git branch -a --format='%(refname:short)'", workingDirectory, cancellationToken);
            var branches = branchesOutput.Success
                ? branchesOutput.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(b => b.Trim().Trim('\''))
                    .Where(b => b.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

            return new GitBranchResult(true, currentBranch, branches, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[GIT] Branches error: {ex.Message}");
            return new GitBranchResult(false, null, [], ex.Message);
        }
    }

    public async Task<GitCommitResult> CommitAsync(string workingDirectory, string message, bool amend = false, CancellationToken cancellationToken = default)
    {
        var protectionCheck = await CheckBranchProtection(workingDirectory, cancellationToken);
        if (!protectionCheck.Success)
        {
            return new GitCommitResult(false, null, protectionCheck.Error);
        }

        try
        {
            var cmd = amend ? $"git commit --amend -m \"{EscapeMessage(message)}\"" : $"git commit -m \"{EscapeMessage(message)}\"";
            var result = await RunAsync(cmd, workingDirectory, cancellationToken);

            if (!result.Success)
            {
                return new GitCommitResult(false, null, result.StandardError);
            }

            var hashResult = await RunAsync("git rev-parse HEAD", workingDirectory, cancellationToken);
            var commitHash = hashResult.Success ? hashResult.StandardOutput.Trim() : null;

            logger.Log($"[GIT] Committed {commitHash?[..7]}: {message[..Math.Min(50, message.Length)]}");
            return new GitCommitResult(true, commitHash, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[GIT] Commit error: {ex.Message}");
            return new GitCommitResult(false, null, ex.Message);
        }
    }

    public async Task<GitCommitResult> CommitAllAsync(string workingDirectory, string message, CancellationToken cancellationToken = default)
    {
        var protectionCheck = await CheckBranchProtection(workingDirectory, cancellationToken);
        if (!protectionCheck.Success)
        {
            return new GitCommitResult(false, null, protectionCheck.Error);
        }

        try
        {
            var addResult = await RunAsync("git add -A", workingDirectory, cancellationToken);
            if (!addResult.Success)
            {
                return new GitCommitResult(false, null, addResult.StandardError ?? "git add failed");
            }

            return await CommitAsync(workingDirectory, message, false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[GIT] CommitAll error: {ex.Message}");
            return new GitCommitResult(false, null, ex.Message);
        }
    }

    public async Task<GitRevertResult> RevertAsync(string workingDirectory, string commitHash, CancellationToken cancellationToken = default)
    {
        var protectionCheck = await CheckBranchProtection(workingDirectory, cancellationToken);
        if (!protectionCheck.Success)
        {
            return new GitRevertResult(false, null, protectionCheck.Error);
        }

        try
        {
            var result = await RunAsync($"git revert --no-edit {commitHash}", workingDirectory, cancellationToken);
            if (!result.Success)
            {
                return new GitRevertResult(false, null, result.StandardError);
            }

            var newHashResult = await RunAsync("git rev-parse HEAD", workingDirectory, cancellationToken);
            var newHash = newHashResult.Success ? newHashResult.StandardOutput.Trim() : null;

            logger.Log($"[GIT] Reverted {commitHash[..7]} -> {newHash?[..7]}");
            return new GitRevertResult(true, newHash, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[GIT] Revert error: {ex.Message}");
            return new GitRevertResult(false, null, ex.Message);
        }
    }

    public async Task<GitDiffResult> GetDiffAsync(string workingDirectory, bool staged = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return new GitDiffResult(false, [], null, "Working directory does not exist.");
        }

        try
        {
            var cmd = staged ? "git diff --cached --name-only" : "git diff --name-only";
            var nameOnly = await RunAsync(cmd, workingDirectory, cancellationToken);
            var diffStat = await RunAsync(staged ? "git diff --cached --stat" : "git diff --stat", workingDirectory, cancellationToken);

            if (!nameOnly.Success)
            {
                return new GitDiffResult(false, [], null, nameOnly.StandardError ?? "git diff failed");
            }

            var changedFiles = nameOnly.StandardOutput
                .Split("\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            var diffStatOutput = diffStat.Success ? diffStat.StandardOutput.Trim() : null;

            return new GitDiffResult(true, changedFiles, diffStatOutput, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[GIT] Diff error: {ex.Message}");
            return new GitDiffResult(false, [], null, ex.Message);
        }
    }

    public async Task<GitLogResult> GetLogAsync(string workingDirectory, int count = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return new GitLogResult(false, [], "Working directory does not exist.");
        }

        try
        {
            var result = await RunAsync($"git log -{count} --pretty=format:'%H|%an|%ad|%s' --date=short", workingDirectory, cancellationToken);
            if (!result.Success)
            {
                return new GitLogResult(false, [], result.StandardError ?? "git log failed");
            }

            var entries = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var parts = line.Split('|', 4);
                    return parts.Length == 4
                        ? new GitLogEntry(parts[0], parts[1], parts[2], parts[3])
                        : new GitLogEntry(parts[0], "", "", line);
                })
                .ToList();

            return new GitLogResult(true, entries, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[GIT] Log error: {ex.Message}");
            return new GitLogResult(false, [], ex.Message);
        }
    }

    public async Task<GitRemoteResult> GetRemotesAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return new GitRemoteResult(false, [], "Working directory does not exist.");
        }

        try
        {
            var result = await RunAsync("git remote -v", workingDirectory, cancellationToken);
            if (!result.Success)
            {
                return new GitRemoteResult(false, [], result.StandardError ?? "git remote failed");
            }

            var remotes = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("(fetch)"))
                .Select(line =>
                {
                    var parts = line.Split('\t');
                    var name = parts[0].Trim();
                    var url = parts.Length > 1 ? parts[1].Replace(" (fetch)", "").Trim() : "";
                    return new GitRemote(name, url);
                })
                .DistinctBy(r => r.Name)
                .ToList();

            return new GitRemoteResult(true, remotes, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[GIT] Remotes error: {ex.Message}");
            return new GitRemoteResult(false, [], ex.Message);
        }
    }

    public async Task<string> GenerateCommitMessageAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (llamaClient == null)
        {
            return GenerateConventionalCommitMessage(workingDirectory);
        }

        try
        {
            var diffResult = await GetDiffAsync(workingDirectory, true, cancellationToken);
            if (!diffResult.IsRepository || string.IsNullOrWhiteSpace(diffResult.DiffStat))
            {
                return GenerateConventionalCommitMessage(workingDirectory);
            }

            var prompt = $$"""
                Generate a concise conventional commit message for the following staged changes.
                Format: <type>(<scope>): <subject>
                Types: feat, fix, docs, style, refactor, test, chore, perf, build, ci, revert
                Keep subject under 72 chars. No explanation, just the message.

                Changes:
                {{diffResult.DiffStat[..Math.Min(3000, diffResult.DiffStat.Length)]}}
                """;

            var response = await llamaClient.GetResponseAsync(prompt, progress: null, cancellationToken);
            var message = response?.Trim().Trim('"', '\'', '`') ?? GenerateConventionalCommitMessage(workingDirectory);

            if (!IsValidConventionalCommit(message))
            {
                message = GenerateConventionalCommitMessage(workingDirectory);
            }

            return message;
        }
        catch
        {
            return GenerateConventionalCommitMessage(workingDirectory);
        }
    }

    public async Task<bool> IsProtectedBranchAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var branch = await GetCurrentBranchAsync(workingDirectory, cancellationToken);
        return !string.IsNullOrWhiteSpace(branch) && _protectedBranches.Contains(branch);
    }

    public async Task<string> GetCurrentBranchAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return string.Empty;
        }

        try
        {
            var result = await RunAsync("git rev-parse --abbrev-ref HEAD", workingDirectory, cancellationToken);
            return result.Success ? result.StandardOutput.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<(bool Success, string? Error)> CheckBranchProtection(string workingDirectory, CancellationToken cancellationToken)
    {
        var branch = await GetCurrentBranchAsync(workingDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return (false, "Could not determine current branch.");
        }

        if (_protectedBranches.Contains(branch))
        {
            return (false, $"Branch '{branch}' is protected. Commits to main/master/develop/release branches are blocked. Create a feature branch instead.");
        }

        return (true, null);
    }

    private async Task<ShellCommandResult> RunAsync(string command, string workingDirectory, CancellationToken cancellationToken)
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

    private static string EscapeMessage(string message)
    {
        return message.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }

    private static bool IsValidConventionalCommit(string message)
    {
        var pattern = @"^(feat|fix|docs|style|refactor|test|chore|perf|build|ci|revert)(\(.+\))?: .{1,72}";
        return System.Text.RegularExpressions.Regex.IsMatch(message, pattern);
    }

    private string GenerateConventionalCommitMessage(string workingDirectory)
    {
        try
        {
            var statusResult = GetStatusAsync(workingDirectory).GetAwaiter().GetResult();
            if (!statusResult.IsRepository || !statusResult.HasUncommittedChanges)
            {
                return "chore: update";
            }

            var allFiles = statusResult.StagedFiles.Concat(statusResult.UnstagedFiles).Concat(statusResult.UntrackedFiles).ToList();
            var hasTests = allFiles.Any(f => f.Contains("test", StringComparison.OrdinalIgnoreCase) || f.Contains("spec", StringComparison.OrdinalIgnoreCase));
            var hasDocs = allFiles.Any(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            var hasConfig = allFiles.Any(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));
            var hasSource = allFiles.Any(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".py", StringComparison.OrdinalIgnoreCase));

            if (hasTests) return "test: update tests";
            if (hasDocs) return "docs: update documentation";
            if (hasConfig) return "chore: update configuration";
            if (hasSource) return "feat: update source code";

            return "chore: update files";
        }
        catch
        {
            return "chore: update";
        }
    }
}