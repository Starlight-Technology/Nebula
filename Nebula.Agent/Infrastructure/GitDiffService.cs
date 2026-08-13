using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Infrastructure;

public sealed class GitDiffService(
    IShellExecutor executor,
    ILogger logger) : IGitDiffService
{
    public async Task<GitDiffResult> GetWorkingTreeDiffAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            !Directory.Exists(workingDirectory))
        {
            return GitDiffResult.NotRepository(
                "Working directory does not exist; git diff skipped.");
        }

        try
        {
            var repositoryCheck = await RunAsync(
                "git rev-parse --is-inside-work-tree",
                workingDirectory,
                cancellationToken);
            if (!repositoryCheck.Success)
            {
                return GitDiffResult.NotRepository();
            }

            var nameOnly = await RunAsync(
                "git diff --name-only",
                workingDirectory,
                cancellationToken);
            var diffStat = await RunAsync(
                "git diff --stat",
                workingDirectory,
                cancellationToken);

            var changedFiles = nameOnly.Success
                ? nameOnly.StandardOutput
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList()
                : [];

            return new GitDiffResult(
                true,
                changedFiles,
                diffStat.Success && !string.IsNullOrWhiteSpace(diffStat.StandardOutput)
                    ? diffStat.StandardOutput.Trim()
                    : null,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[GIT_DIFF] Unable to inspect working tree: {ex.Message}");
            return GitDiffResult.NotRepository(ex.Message);
        }
    }

    private async Task<ShellCommandResult> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (executor is IDetailedShellExecutor detailedExecutor)
        {
            return await detailedExecutor.RunCommandDetailedAsync(
                command,
                workingDirectory,
                cancellationToken);
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
}
