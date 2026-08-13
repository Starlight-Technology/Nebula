using Moq;
using Nebula.Agent.Infrastructure;
using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class GitDiffServiceTest
{
    [Fact]
    public async Task must_return_not_repository_when_directory_does_not_exist()
    {
        var executorMock = new Mock<IShellExecutor>();
        var service = new GitDiffService(
            executorMock.Object,
            new TestLogger());

        var result = await service.GetWorkingTreeDiffAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.False(result.IsRepository);
        executorMock.Verify(
            executor => executor.RunCommandAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task must_return_not_repository_when_not_inside_git_work_tree()
    {
        using var workspace = new TempTestWorkspace();
        var executorMock = new Mock<IShellExecutor>();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "git rev-parse --is-inside-work-tree",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("false");

        var service = new GitDiffService(
            executorMock.Object,
            new TestLogger());

        var result = await service.GetWorkingTreeDiffAsync(workspace.Path);

        Assert.False(result.IsRepository);
    }

    [Fact]
    public async Task must_return_changed_files_and_diff_stat()
    {
        using var workspace = new TempTestWorkspace();
        var detailedMock = new Mock<IShellExecutor>();
        detailedMock.As<IDetailedShellExecutor>();
        detailedMock
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                "git rev-parse --is-inside-work-tree",
                workspace.Path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShellCommandResult
            {
                Command = "git rev-parse --is-inside-work-tree",
                WorkingDirectory = workspace.Path,
                StandardOutput = "true",
                ExitCode = 0
            });
        detailedMock
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                "git diff --name-only",
                workspace.Path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShellCommandResult
            {
                Command = "git diff --name-only",
                WorkingDirectory = workspace.Path,
                StandardOutput = "Program.cs\nREADME.md",
                ExitCode = 0
            });
        detailedMock
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                "git diff --stat",
                workspace.Path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShellCommandResult
            {
                Command = "git diff --stat",
                WorkingDirectory = workspace.Path,
                StandardOutput = " Program.cs | 3 ++-",
                ExitCode = 0
            });

        var service = new GitDiffService(
            detailedMock.Object,
            new TestLogger());

        var result = await service.GetWorkingTreeDiffAsync(workspace.Path);

        Assert.True(result.IsRepository);
        Assert.Equal(["Program.cs", "README.md"], result.ChangedFiles);
        Assert.Contains("Program.cs", result.DiffStat);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task must_return_not_repository_when_command_fails()
    {
        using var workspace = new TempTestWorkspace();
        var executorMock = new Mock<IShellExecutor>();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("git not available"));

        var service = new GitDiffService(
            executorMock.Object,
            new TestLogger());

        var result = await service.GetWorkingTreeDiffAsync(workspace.Path);

        Assert.False(result.IsRepository);
        Assert.Contains("git not available", result.Error);
    }

    private sealed class TestLogger : ILogger
    {
        public void Log(string message)
        {
        }

        public void LogError(string message)
        {
        }
    }
}
