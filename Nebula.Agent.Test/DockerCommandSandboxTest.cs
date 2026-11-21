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

    [Theory]
    [InlineData("--privileged alpine")]
    [InlineData("alpine; touch /tmp/pwned")]
    public async Task run_sandboxed_async_must_reject_invalid_image(string image)
    {
        var sandbox = CreateSandbox(new NebulaRuntimeSettings
        {
            SandboxMode = SandboxMode.Docker,
            SandboxImage = image
        });
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        var command = new ResolvedCommand(
            "bash",
            "-c \"echo oi\"",
            "echo oi",
            workspace,
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.RunSandboxedAsync(
                ShellKind.Bash,
                command,
                workspace,
                CancellationToken.None));
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
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        var command = new ResolvedCommand(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"echo oi\"",
            "echo oi",
            workspace,
            []);

        var result = await sandbox.RunSandboxedAsync(
            ShellKind.PowerShell,
            command,
            workspace,
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
        Assert.Contains($"\"{workspace}:/workspace:rw\"", captured.Arguments);
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
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "opencode",
            "nebula-manual");
        var targetFile = Path.Combine(
            workspace,
            "web-project",
            "index.html");
        var targetFileForward = targetFile.Replace('\\', '/');
        var command = new ResolvedCommand(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"New-Item -ItemType File -Path '{targetFileForward}'\"",
            $"New-Item -ItemType File -Path '{targetFileForward}'",
            workspace,
            []);

        await sandbox.RunSandboxedAsync(
            ShellKind.PowerShell,
            command,
            workspace,
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("/workspace/web-project/index.html", captured.Arguments);
        Assert.DoesNotContain(
            targetFileForward,
            captured.Arguments,
            StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task run_sandboxed_async_must_use_podman_and_relabel_workspace()
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
                    StandardOutput = "podman output",
                    ExitCode = 0
                };
            });

        var sandbox = CreateSandbox(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            executorMock.Object,
            new SandboxRuntimeInfo("podman", "podman", true));
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        var command = new ResolvedCommand(
            "bash",
            "-c \"echo oi\"",
            "echo oi",
            workspace,
            []);

        var result = await sandbox.RunSandboxedAsync(
            ShellKind.Bash,
            command,
            workspace,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(captured);
        Assert.Equal("podman", captured.FileName);
        Assert.Contains($":/workspace:rw,Z", captured.Arguments);
        Assert.Contains("(sandbox podman)", captured.DisplayCommand);
    }

    [Fact]
    public async Task run_sandboxed_async_must_use_host_mount_when_provided()
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
                    ExitCode = 0
                };
            });

        var sandbox = CreateSandbox(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            executorMock.Object);
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        var hostWorkspace = Path.Combine(
            Path.GetTempPath(),
            "nebula-host-workspace");
        Directory.CreateDirectory(hostWorkspace);
        try
        {
            var command = new ResolvedCommand(
                "bash",
                "-c \"echo oi\"",
                "echo oi",
                workspace,
                []);

            await sandbox.RunSandboxedAsync(
                ShellKind.Bash,
                command,
                workspace,
                new SandboxWorkspaceMount(hostWorkspace, Path.GetTempPath()),
                CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Contains(
                $"\"{hostWorkspace}:/workspace:rw\"",
                captured.Arguments);
        }
        finally
        {
            Directory.Delete(hostWorkspace, recursive: true);
        }
    }

    [Fact]
    public async Task run_sandboxed_async_must_reject_host_mount_outside_allowed_root()
    {
        var executorMock = new Mock<IResolvedCommandExecutor>();
        var sandbox = CreateSandbox(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            executorMock.Object);
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        var command = new ResolvedCommand(
            "bash",
            "-c \"echo oi\"",
            "echo oi",
            workspace,
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.RunSandboxedAsync(
                ShellKind.Bash,
                command,
                workspace,
                new SandboxWorkspaceMount(
                    "/outside/workspace",
                    Path.GetTempPath()),
                CancellationToken.None));

        executorMock.Verify(
            executor => executor.RunCommandDetailedAsync(
                It.IsAny<ResolvedCommand>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_sandboxed_async_must_fail_without_container_runtime()
    {
        var executorMock = new Mock<IResolvedCommandExecutor>();
        var sandbox = CreateSandboxWithoutRuntime(
            new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            executorMock.Object);
        var workspace = Path.GetTempPath().TrimEnd('\\', '/');
        var command = new ResolvedCommand(
            "bash",
            "-c \"echo oi\"",
            "echo oi",
            workspace,
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.RunSandboxedAsync(
                ShellKind.Bash,
                command,
                workspace,
                CancellationToken.None));

        executorMock.Verify(
            executor => executor.RunCommandDetailedAsync(
                It.IsAny<ResolvedCommand>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static DockerCommandSandbox CreateSandbox(
        NebulaRuntimeSettings settings,
        IResolvedCommandExecutor? executor = null,
        SandboxRuntimeInfo? runtime = null)
    {
        return new DockerCommandSandbox(
            executor ?? new Mock<IResolvedCommandExecutor>().Object,
            settings,
            new FakeSandboxRuntimeLocator(
                runtime ?? new SandboxRuntimeInfo("docker", "docker", false)));
    }

    private static DockerCommandSandbox CreateSandboxWithoutRuntime(
        NebulaRuntimeSettings settings,
        IResolvedCommandExecutor executor)
    {
        return new DockerCommandSandbox(
            executor,
            settings,
            new FakeSandboxRuntimeLocator(null));
    }

    private sealed class FakeSandboxRuntimeLocator(
        SandboxRuntimeInfo? runtime) : ISandboxRuntimeLocator
    {
        public SandboxRuntimeInfo? Locate(SandboxContainerRuntime preference) =>
            runtime;
    }
}
