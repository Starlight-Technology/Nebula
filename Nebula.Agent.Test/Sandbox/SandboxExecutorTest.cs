using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Core.Sandbox;
using Nebula.Runner;
using Nebula.Sandbox;

namespace Nebula.Agent.Test.Sandbox;

public sealed class SandboxExecutorTest
{
    [Fact]
    public async Task run_must_map_workspace_to_host_and_return_runtime()
    {
        using var fixture = new ExecutorFixture();
        var workspace = fixture.CreateContainerWorkspace();
        var executor = fixture.CreateExecutor(
            new SandboxRuntimeInfo("podman", "podman", true));

        var result = await executor.RunAsync(
            CreateRequest(workspace, workspace),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("podman", result.Runtime);
        Assert.NotNull(fixture.CommandExecutor.LastCommand);
        Assert.Contains(
            $"\"{fixture.HostWorkspace(workspace)}:/workspace:rw,Z\"",
            fixture.CommandExecutor.LastCommand.Arguments);
        Assert.Contains("-w /workspace", fixture.CommandExecutor.LastCommand.Arguments);
    }

    [Fact]
    public async Task run_must_reject_workspace_outside_container_root()
    {
        using var fixture = new ExecutorFixture();
        var executor = fixture.CreateExecutor(
            new SandboxRuntimeInfo("podman", "podman", true));
        var outside = Path.Combine(fixture.ContainerRoot, "..", "outside");

        var exception = await Assert.ThrowsAsync<SandboxRequestException>(() =>
            executor.RunAsync(
                CreateRequest(outside, outside),
                CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task run_must_reject_working_directory_outside_workspace()
    {
        using var fixture = new ExecutorFixture();
        var workspace = fixture.CreateContainerWorkspace();
        var executor = fixture.CreateExecutor(
            new SandboxRuntimeInfo("podman", "podman", true));

        var exception = await Assert.ThrowsAsync<SandboxRequestException>(() =>
            executor.RunAsync(
                CreateRequest(workspace, fixture.ContainerRoot),
                CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task run_must_reject_invalid_image()
    {
        using var fixture = new ExecutorFixture();
        var workspace = fixture.CreateContainerWorkspace();
        var executor = fixture.CreateExecutor(
            new SandboxRuntimeInfo("podman", "podman", true));

        var exception = await Assert.ThrowsAsync<SandboxRequestException>(() =>
            executor.RunAsync(
                CreateRequest(workspace, workspace, "alpine; rm -rf /"),
                CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task run_must_fail_when_runtime_is_missing()
    {
        using var fixture = new ExecutorFixture();
        var workspace = fixture.CreateContainerWorkspace();
        var executor = fixture.CreateExecutor(runtime: null);

        var exception = await Assert.ThrowsAsync<SandboxRequestException>(() =>
            executor.RunAsync(
                CreateRequest(workspace, workspace),
                CancellationToken.None));

        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task run_must_return_timeout_when_command_exceeds_limit()
    {
        using var fixture = new ExecutorFixture();
        var workspace = fixture.CreateContainerWorkspace();
        var executor = fixture.CreateExecutor(
            new SandboxRuntimeInfo("podman", "podman", true),
            maxTimeoutSeconds: 1,
            handler: async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ShellCommandResult();
            });

        var exception = await Assert.ThrowsAsync<SandboxRequestException>(() =>
            executor.RunAsync(
                CreateRequest(workspace, workspace, timeoutSeconds: 1),
                CancellationToken.None));

        Assert.Equal(504, exception.StatusCode);
    }

    [Fact]
    public async Task run_must_truncate_output()
    {
        using var fixture = new ExecutorFixture();
        var workspace = fixture.CreateContainerWorkspace();
        var executor = fixture.CreateExecutor(
            new SandboxRuntimeInfo("podman", "podman", true),
            maxOutputChars: 10,
            handler: (_, _) => Task.FromResult(new ShellCommandResult
            {
                StandardOutput = "123456789012345",
                StandardError = "abcdefghijklmno",
                ExitCode = 0
            }));

        var result = await executor.RunAsync(
            CreateRequest(workspace, workspace),
            CancellationToken.None);

        Assert.Equal("1234567890\n...[truncated]", result.StandardOutput);
        Assert.Equal("abcdefghij\n...[truncated]", result.StandardError);
    }

    [Fact]
    public async Task probe_must_report_runtime_availability()
    {
        using var fixture = new ExecutorFixture();
        var executor = fixture.CreateExecutor(
            new SandboxRuntimeInfo("podman", "podman", true),
            handler: (_, _) => Task.FromResult(new ShellCommandResult
            {
                ExitCode = 0
            }));

        var probe = await executor.ProbeRuntimeAsync(CancellationToken.None);

        Assert.True(probe.Reachable);
        Assert.Equal("podman", probe.Runtime);
        Assert.Null(probe.Error);
    }

    [Fact]
    public async Task probe_must_report_missing_runtime()
    {
        using var fixture = new ExecutorFixture();
        var executor = fixture.CreateExecutor(runtime: null);

        var probe = await executor.ProbeRuntimeAsync(CancellationToken.None);

        Assert.False(probe.Reachable);
        Assert.Null(probe.Runtime);
        Assert.NotNull(probe.Error);
    }

    private static SandboxRunRequest CreateRequest(
        string workspace,
        string workingDirectory,
        string image = "mcr.microsoft.com/dotnet/sdk:10.0-noble",
        int timeoutSeconds = 30) =>
        new(
            ShellKind.Bash,
            new SandboxCommandRequest(
                "bash",
                "-c \"echo hi\"",
                "echo hi",
                workingDirectory,
                []),
            workspace,
            image,
            0,
            0,
            timeoutSeconds);

    private sealed class ExecutorFixture : IDisposable
    {
        public ExecutorFixture()
        {
            ContainerRoot = CreateDirectory("container");
            HostRoot = CreateDirectory("host");
            CommandExecutor = new CapturingExecutor();
        }

        public string ContainerRoot { get; }

        public string HostRoot { get; }

        public CapturingExecutor CommandExecutor { get; }

        public SandboxExecutor CreateExecutor(
            SandboxRuntimeInfo? runtime,
            int maxTimeoutSeconds = 300,
            int maxOutputChars = 1_000_000,
            Func<ResolvedCommand, CancellationToken, Task<ShellCommandResult>>? handler = null)
        {
            CommandExecutor.Handler = handler ??
                ((_, _) => Task.FromResult(new ShellCommandResult
                {
                    ExitCode = 0,
                    StandardOutput = "ok"
                }));
            return new SandboxExecutor(
                new SandboxOptions
                {
                    Token = "test-token",
                    Runtime = SandboxContainerRuntime.Podman,
                    ContainerTempRoot = ContainerRoot,
                    HostTempRoot = HostRoot,
                    MaxTimeoutSeconds = maxTimeoutSeconds,
                    MaxOutputChars = maxOutputChars
                },
                new FakeRuntimeLocator(runtime),
                CommandExecutor);
        }

        public string CreateContainerWorkspace() =>
            Directory.CreateDirectory(
                Path.Combine(ContainerRoot, "workspace")).FullName;

        public string HostWorkspace(string containerWorkspace) =>
            Path.Combine(
                HostRoot,
                Path.GetRelativePath(ContainerRoot, containerWorkspace));

        public void Dispose()
        {
            TryDelete(ContainerRoot);
            TryDelete(HostRoot);
        }

        private static string CreateDirectory(string prefix) =>
            Directory.CreateDirectory(
                Path.Combine(
                    Path.GetTempPath(),
                    $"nebula-sandbox-executor-{prefix}-{Guid.NewGuid():N}"))
                .FullName;

        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class CapturingExecutor : IResolvedCommandExecutor
    {
        public Func<ResolvedCommand, CancellationToken, Task<ShellCommandResult>>
            Handler { get; set; } =
            (_, _) => Task.FromResult(new ShellCommandResult());

        public ResolvedCommand? LastCommand { get; private set; }

        public Task<string> RunCommandAsync(string command) =>
            throw new NotSupportedException();

        public Task<string> RunCommandAsync(
            string command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ShellCommandResult> RunCommandDetailedAsync(
            string command,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ShellCommandResult> RunCommandDetailedAsync(
            ResolvedCommand command,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Handler(command, cancellationToken);
        }
    }

    private sealed class FakeRuntimeLocator(
        SandboxRuntimeInfo? runtime) : ISandboxRuntimeLocator
    {
        public SandboxRuntimeInfo? Locate(SandboxContainerRuntime preference) =>
            runtime;
    }
}
