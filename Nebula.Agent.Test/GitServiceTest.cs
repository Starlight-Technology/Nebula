using Moq;
using Nebula.Agent.Infrastructure;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class GitServiceTest
{
    [Fact]
    public async Task RevertFileAsync_restores_unstaged_file_from_head()
    {
        using var workspace = new TempTestWorkspace();
        string? executedCommand = null;
        var executor = CreateExecutor(
            workspace.Path,
            "feature/revert",
            command => executedCommand = command);

        var service = new GitService(executor.Object, new TestLogger());

        var result = await service.RevertFileAsync(
            workspace.Path,
            "src/Program.cs");

        Assert.True(result.Success);
        Assert.Equal("src/Program.cs", result.FilePath);
        Assert.Null(result.CommitHash);
        Assert.Null(result.Error);
        Assert.Equal(
            "git restore --worktree -- \"src/Program.cs\"",
            executedCommand);
    }

    [Fact]
    public async Task RevertFileAsync_restores_staged_and_worktree()
    {
        using var workspace = new TempTestWorkspace();
        string? executedCommand = null;
        var executor = CreateExecutor(
            workspace.Path,
            "feature/revert",
            command => executedCommand = command);

        var service = new GitService(executor.Object, new TestLogger());

        var result = await service.RevertFileAsync(
            workspace.Path,
            "src/Program.cs",
            staged: true);

        Assert.True(result.Success);
        Assert.Equal(
            "git restore --staged --worktree -- \"src/Program.cs\"",
            executedCommand);
    }

    [Fact]
    public async Task RevertFileAsync_restores_from_specific_commit()
    {
        using var workspace = new TempTestWorkspace();
        string? executedCommand = null;
        var executor = CreateExecutor(
            workspace.Path,
            "feature/revert",
            command => executedCommand = command);

        var service = new GitService(executor.Object, new TestLogger());

        var result = await service.RevertFileAsync(
            workspace.Path,
            "src/Program.cs",
            commitHash: "abc1234");

        Assert.True(result.Success);
        Assert.Equal("abc1234", result.CommitHash);
        Assert.Equal(
            "git restore --source=abc1234 --worktree -- \"src/Program.cs\"",
            executedCommand);
    }

    [Fact]
    public async Task RevertFileAsync_blocks_protected_branch()
    {
        using var workspace = new TempTestWorkspace();
        var restoreCalls = 0;
        var executor = CreateExecutor(
            workspace.Path,
            "main",
            _ => restoreCalls++);

        var service = new GitService(executor.Object, new TestLogger());

        var result = await service.RevertFileAsync(
            workspace.Path,
            "src/Program.cs");

        Assert.False(result.Success);
        Assert.Contains("protected", result.Error);
        Assert.Equal(0, restoreCalls);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData(".git/config")]
    [InlineData("/etc/passwd")]
    [InlineData("src/Program.cs; rm -rf /")]
    [InlineData("src/Program.cs\"")]
    public async Task RevertFileAsync_rejects_unsafe_paths(string path)
    {
        using var workspace = new TempTestWorkspace();
        var restoreCalls = 0;
        var executor = CreateExecutor(
            workspace.Path,
            "feature/revert",
            _ => restoreCalls++);

        var service = new GitService(executor.Object, new TestLogger());

        var result = await service.RevertFileAsync(workspace.Path, path);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(0, restoreCalls);
    }

    [Fact]
    public async Task RevertFileAsync_rejects_invalid_commit_hash()
    {
        using var workspace = new TempTestWorkspace();
        var restoreCalls = 0;
        var executor = CreateExecutor(
            workspace.Path,
            "feature/revert",
            _ => restoreCalls++);

        var service = new GitService(executor.Object, new TestLogger());

        var result = await service.RevertFileAsync(
            workspace.Path,
            "src/Program.cs",
            commitHash: "abc123; rm -rf /");

        Assert.False(result.Success);
        Assert.Contains("Commit hash", result.Error);
        Assert.Equal(0, restoreCalls);
    }

    [Fact]
    public async Task RevertFileAsync_returns_git_error_when_restore_fails()
    {
        using var workspace = new TempTestWorkspace();
        var executor = CreateExecutor(
            workspace.Path,
            "feature/revert",
            _ => { },
            exitCode: 1,
            standardError: "pathspec did not match");

        var service = new GitService(executor.Object, new TestLogger());

        var result = await service.RevertFileAsync(
            workspace.Path,
            "missing.cs");

        Assert.False(result.Success);
        Assert.Equal("missing.cs", result.FilePath);
        Assert.Contains("pathspec did not match", result.Error);
    }

    private static Mock<IShellExecutor> CreateExecutor(
        string workspace,
        string branch,
        Action<string>? onRestore,
        int exitCode = 0,
        string standardError = "")
    {
        var executor = new Mock<IShellExecutor>();
        var detailed = executor.As<IDetailedShellExecutor>();

        detailed
            .Setup(detail => detail.RunCommandDetailedAsync(
                "git rev-parse --abbrev-ref HEAD",
                workspace,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShellCommandResult
            {
                Command = "git rev-parse --abbrev-ref HEAD",
                WorkingDirectory = workspace,
                StandardOutput = branch,
                ExitCode = 0
            });

        detailed
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.Is<string>(command => command.StartsWith("git restore")),
                workspace,
                It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((command, _, _) =>
            {
                onRestore?.Invoke(command);
                return Task.FromResult(new ShellCommandResult
                {
                    Command = command,
                    WorkingDirectory = workspace,
                    StandardError = standardError,
                    ExitCode = exitCode
                });
            });

        return executor;
    }

    private sealed class TestLogger : Nebula.Agent.ILogger
    {
        public void Log(string message)
        {
        }

        public void LogError(string message)
        {
        }
    }
}
