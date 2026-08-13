using Moq;

using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class DockerCommandSandboxTest
{
    [Theory]
    [InlineData(ShellKind.PowerShell, true)]
    [InlineData(ShellKind.Bash, true)]
    [InlineData(ShellKind.Sh, true)]
    [InlineData(ShellKind.Cmd, false)]
    [InlineData(ShellKind.Unknown, false)]
    public void is_eligible_must_follow_shell_kind(ShellKind shellKind, bool expected)
    {
        var sandbox = CreateSandbox(new NebulaRuntimeSettings
        {
            SandboxMode = SandboxMode.Docker
        });

        Assert.Equal(expected, sandbox.IsEligible(shellKind));
    }

    [Fact]
    public void mode_must_come_from_settings()
    {
        var sandbox = CreateSandbox(new NebulaRuntimeSettings
        {
            SandboxMode = SandboxMode.Docker
        });

        Assert.Equal(SandboxMode.Docker, sandbox.Mode);
    }

    [Fact]
    public async Task run_sandboxed_async_must_invoke_docker_with_isolation_flags()
    {
        var executorMock = new Mock<IResolvedCommandExecutor>();
        ResolvedCommand? captured = null;
        executorMock
            .Setup(executor => executor.RunCommandDetailedAsync(
                It.IsAny<ResolvedCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedCommand command, CancellationToken _) =>
            {
                captured = command;
                return new ShellCommandResult
                {
                    Command = command.DisplayCommand,
                    WorkingDirectory = command.WorkingDirectory,
                    StandardOutput = "sandbox output",
                    ExitCode = 0
                };
            });

        var sandbox = CreateSandbox(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker,
                SandboxImage = "mcr.microsoft.com/powershell:lts",
                SandboxMemoryLimitMb = 512,
                SandboxCpuLimit = 1
            },
            executorMock.Object);
        var command = new ResolvedCommand(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"echo oi\"",
            "echo oi",
            "C:\\workspace",
            []);

        var result = await sandbox.RunSandboxedAsync(
            ShellKind.PowerShell,
            command,
            "C:\\workspace",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("sandbox output", result.StandardOutput);
        Assert.NotNull(captured);
        Assert.Equal("docker", captured.FileName);
        Assert.Contains("--rm", captured.Arguments);
        Assert.Contains("--network none", captured.Arguments);
        Assert.Contains("--cap-drop ALL", captured.Arguments);
        Assert.Contains("--security-opt no-new-privileges", captured.Arguments);
        Assert.Contains("--memory 512m", captured.Arguments);
        Assert.Contains("--cpus 1", captured.Arguments);
        Assert.Contains("\"C:\\workspace:/workspace:rw\"", captured.Arguments);
        Assert.Contains("-w /workspace", captured.Arguments);
        Assert.Contains("mcr.microsoft.com/powershell:lts", captured.Arguments);
        Assert.Contains("pwsh -NoProfile -NonInteractive -Command", captured.Arguments);
        Assert.Contains("echo oi", captured.Arguments);
        Assert.Contains("(sandbox docker)", captured.DisplayCommand);
    }

    [Fact]
    public async Task run_sandboxed_async_must_use_bash_for_bash_shells()
    {
        var executorMock = new Mock<IResolvedCommandExecutor>();
        ResolvedCommand? captured = null;
        executorMock
            .Setup(executor => executor.RunCommandDetailedAsync(
                It.IsAny<ResolvedCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedCommand command, CancellationToken _) =>
            {
                captured = command;
                return new ShellCommandResult
                {
                    Command = command.DisplayCommand,
                    WorkingDirectory = command.WorkingDirectory,
                    StandardOutput = string.Empty,
                    ExitCode = 0
                };
            });

        var sandbox = CreateSandbox(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            executorMock.Object);
        var command = new ResolvedCommand(
            "/bin/bash",
            "-c \"ls\"",
            "ls",
            "/workspace",
            []);

        await sandbox.RunSandboxedAsync(
            ShellKind.Bash,
            command,
            "/workspace",
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("bash -c", captured.Arguments);
        Assert.DoesNotContain("pwsh", captured.Arguments);
    }

    [Fact]
    public async Task run_sandboxed_async_must_not_add_limits_when_not_configured()
    {
        var executorMock = new Mock<IResolvedCommandExecutor>();
        ResolvedCommand? captured = null;
        executorMock
            .Setup(executor => executor.RunCommandDetailedAsync(
                It.IsAny<ResolvedCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedCommand command, CancellationToken _) =>
            {
                captured = command;
                return new ShellCommandResult
                {
                    Command = command.DisplayCommand,
                    WorkingDirectory = command.WorkingDirectory,
                    StandardOutput = string.Empty,
                    ExitCode = 0
                };
            });

        var sandbox = CreateSandbox(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            executorMock.Object);

        await sandbox.RunSandboxedAsync(
            ShellKind.PowerShell,
            new ResolvedCommand(
                "powershell.exe",
                "-NoProfile -Command \"pwd\"",
                "pwd",
                "C:\\workspace",
                []),
            "C:\\workspace",
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.DoesNotContain("--memory", captured.Arguments);
        Assert.DoesNotContain("--cpus", captured.Arguments);
    }

    [Fact]
    public async Task run_sandboxed_async_must_unwrap_nested_shell_wrapper()
    {
        var executorMock = new Mock<IResolvedCommandExecutor>();
        ResolvedCommand? captured = null;
        executorMock
            .Setup(executor => executor.RunCommandDetailedAsync(
                It.IsAny<ResolvedCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedCommand command, CancellationToken _) =>
            {
                captured = command;
                return new ShellCommandResult
                {
                    Command = command.DisplayCommand,
                    WorkingDirectory = command.WorkingDirectory,
                    StandardOutput = string.Empty,
                    ExitCode = 0
                };
            });

        var sandbox = CreateSandbox(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            executorMock.Object);
        var command = new ResolvedCommand(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \\\"Get-ChildItem -LiteralPath 'C:/host/data'\\\"\"",
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"Get-ChildItem -LiteralPath 'C:/host/data'\"",
            "C:\\workspace",
            []);

        await sandbox.RunSandboxedAsync(
            ShellKind.PowerShell,
            command,
            "C:\\workspace",
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.DoesNotContain("powershell.exe", captured.Arguments);
        Assert.Contains("Get-ChildItem -LiteralPath 'C:/host/data'", captured.Arguments);
    }

    [Fact]
    public async Task run_sandboxed_async_must_translate_host_paths_to_workspace()
    {
        var executorMock = new Mock<IResolvedCommandExecutor>();
        ResolvedCommand? captured = null;
        executorMock
            .Setup(executor => executor.RunCommandDetailedAsync(
                It.IsAny<ResolvedCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedCommand command, CancellationToken _) =>
            {
                captured = command;
                return new ShellCommandResult
                {
                    Command = command.DisplayCommand,
                    WorkingDirectory = command.WorkingDirectory,
                    StandardOutput = string.Empty,
                    ExitCode = 0
                };
            });

        var sandbox = CreateSandbox(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            executorMock.Object);
        var command = new ResolvedCommand(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"New-Item -ItemType File -Path 'C:/Users/rodri/AppData/Local/Temp/opencode/nebula-manual/web-project/index.html'\"",
            "New-Item -ItemType File -Path 'C:/Users/rodri/AppData/Local/Temp/opencode/nebula-manual/web-project/index.html'",
            "C:\\Users\\rodri\\AppData\\Local\\Temp\\opencode\\nebula-manual",
            []);

        await sandbox.RunSandboxedAsync(
            ShellKind.PowerShell,
            command,
            "C:\\Users\\rodri\\AppData\\Local\\Temp\\opencode\\nebula-manual",
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.DoesNotContain("C:/Users/rodri", captured.Arguments);
        Assert.Contains("web-project/index.html", captured.Arguments);
        Assert.Contains("/workspace/web-project/index.html", captured.Arguments);
    }

    [Fact]
    public async Task run_sandboxed_async_must_not_translate_unrelated_sibling_paths()
    {
        var executorMock = new Mock<IResolvedCommandExecutor>();
        ResolvedCommand? captured = null;
        executorMock
            .Setup(executor => executor.RunCommandDetailedAsync(
                It.IsAny<ResolvedCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedCommand command, CancellationToken _) =>
            {
                captured = command;
                return new ShellCommandResult
                {
                    Command = command.DisplayCommand,
                    WorkingDirectory = command.WorkingDirectory,
                    StandardOutput = string.Empty,
                    ExitCode = 0
                };
            });

        var sandbox = CreateSandbox(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            executorMock.Object);
        var command = new ResolvedCommand(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"Get-Content 'C:/Users/me/AppData/Local/Temp/opencode/nebula-manual-other/file.txt'\"",
            "Get-Content 'C:/Users/me/AppData/Local/Temp/opencode/nebula-manual-other/file.txt'",
            "C:\\Users\\me\\AppData\\Local\\Temp\\opencode\\nebula-manual",
            []);

        await sandbox.RunSandboxedAsync(
            ShellKind.PowerShell,
            command,
            "C:\\Users\\me\\AppData\\Local\\Temp\\opencode\\nebula-manual",
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("nebula-manual-other/file.txt", captured.Arguments);
        Assert.DoesNotContain("/workspace/file.txt", captured.Arguments);
    }

    private static DockerCommandSandbox CreateSandbox(
        NebulaRuntimeSettings settings,
        IResolvedCommandExecutor? executor = null)
    {
        return new DockerCommandSandbox(
            executor ?? new Mock<IResolvedCommandExecutor>().Object,
            settings);
    }
}
