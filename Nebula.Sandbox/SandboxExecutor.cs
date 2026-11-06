using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Core.Safety;
using Nebula.Core.Sandbox;
using Nebula.Runner;

namespace Nebula.Sandbox;

public sealed class SandboxExecutor
{
    private readonly SandboxOptions options;
    private readonly SandboxWorkspacePathMapper? pathMapper;
    private readonly ISandboxRuntimeLocator runtimeLocator;
    private readonly IResolvedCommandExecutor commandExecutor;

    public SandboxExecutor(
        SandboxOptions options,
        ISandboxRuntimeLocator runtimeLocator,
        IResolvedCommandExecutor? commandExecutor = null)
    {
        this.options = options;
        this.runtimeLocator = runtimeLocator;
        this.commandExecutor = commandExecutor ?? new ShellExecutor();
        pathMapper = string.IsNullOrWhiteSpace(options.HostTempRoot)
            ? null
            : new SandboxWorkspacePathMapper(
                options.ContainerTempRoot,
                options.HostTempRoot);
    }

    public SandboxRuntimeInfo? LocateRuntime() =>
        runtimeLocator.Locate(options.Runtime);

    public async Task<SandboxRuntimeProbe> ProbeRuntimeAsync(
        CancellationToken cancellationToken)
    {
        var runtime = LocateRuntime();
        if (runtime is null)
        {
            return new SandboxRuntimeProbe(
                false,
                null,
                $"No container runtime found for preference '{options.Runtime}'.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await commandExecutor.RunCommandDetailedAsync(
                new ResolvedCommand(
                    runtime.Executable,
                    "info",
                    $"{runtime.Name} info",
                    Environment.CurrentDirectory,
                    []),
                timeout.Token);
            return result.ExitCode == 0
                ? new SandboxRuntimeProbe(true, runtime.Name, null)
                : new SandboxRuntimeProbe(
                    false,
                    runtime.Name,
                    string.IsNullOrWhiteSpace(result.StandardError)
                        ? $"Runtime exited with code {result.ExitCode}."
                        : result.StandardError.Trim());
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return new SandboxRuntimeProbe(
                false,
                runtime.Name,
                "Runtime probe timed out.");
        }
        catch (Exception ex)
        {
            return new SandboxRuntimeProbe(false, runtime.Name, ex.Message);
        }
    }

    public async Task<SandboxRunResponse> RunAsync(
        SandboxRunRequest request,
        CancellationToken cancellationToken)
    {
        if (pathMapper is null)
        {
            throw new SandboxRequestException(
                503,
                "SANDBOX_HOST_TEMP_ROOT is not configured.");
        }

        if (!pathMapper.TryMap(request.WorkspacePath, out var hostWorkspace))
        {
            throw new SandboxRequestException(
                400,
                $"Workspace '{request.WorkspacePath}' is outside the allowed root.");
        }

        if (!Directory.Exists(request.WorkspacePath))
        {
            throw new SandboxRequestException(
                400,
                $"Workspace '{request.WorkspacePath}' does not exist.");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(
            request.Command.WorkingDirectory)
            ? request.WorkspacePath
            : request.Command.WorkingDirectory;
        if (!PathContainment.IsUnder(workingDirectory, request.WorkspacePath))
        {
            throw new SandboxRequestException(
                400,
                "Command working directory is outside the workspace.");
        }

        if (!NebulaRuntimeSettings.IsValidSandboxImage(request.Image))
        {
            throw new SandboxRequestException(400, "Sandbox image is invalid.");
        }

        var runtime = LocateRuntime() ??
            throw new SandboxRequestException(
                503,
                $"No container runtime found for preference '{options.Runtime}'.");

        var settings = new NebulaRuntimeSettings
        {
            SandboxMode = SandboxMode.Docker,
            SandboxRuntime = options.Runtime,
            SandboxImage = request.Image,
            SandboxMemoryLimitMb = Math.Max(0, request.MemoryLimitMb),
            SandboxCpuLimit = Math.Max(0, request.CpuLimit)
        };
        var sandbox = new DockerCommandSandbox(
            commandExecutor,
            settings,
            runtimeLocator);
        if (!sandbox.IsEligible(request.ShellKind))
        {
            throw new SandboxRequestException(
                400,
                $"Shell '{request.ShellKind}' is not eligible for sandboxing.");
        }

        var timeoutSeconds = request.TimeoutSeconds > 0
            ? Math.Min(request.TimeoutSeconds, options.MaxTimeoutSeconds)
            : options.MaxTimeoutSeconds;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var result = await sandbox.RunSandboxedAsync(
                request.ShellKind,
                request.Command.ToResolvedCommand(),
                workingDirectory,
                new SandboxWorkspaceMount(hostWorkspace, options.HostTempRoot),
                timeout.Token);

            return new SandboxRunResponse(
                result.ExitCode,
                Truncate(result.StandardOutput),
                Truncate(result.StandardError),
                result.Command,
                workingDirectory,
                result.Timestamp,
                runtime.Name);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new SandboxRequestException(
                504,
                $"Sandboxed command timed out after {timeoutSeconds} seconds.");
        }
    }

    private string Truncate(string value) =>
        value.Length <= options.MaxOutputChars
            ? value
            : value[..options.MaxOutputChars] + "\n...[truncated]";
}

public sealed record SandboxRuntimeProbe(
    bool Reachable,
    string? Runtime,
    string? Error);

public sealed class SandboxRequestException : Exception
{
    public SandboxRequestException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
