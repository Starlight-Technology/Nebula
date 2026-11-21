using Nebula.Core.Commands;

namespace Nebula.Core.Sandbox;

public sealed record SandboxCommandRequest(
    string FileName,
    string Arguments,
    string DisplayCommand,
    string WorkingDirectory,
    IReadOnlyList<string>? Reasons = null)
{
    public ResolvedCommand ToResolvedCommand() =>
        new(
            FileName,
            Arguments,
            DisplayCommand,
            WorkingDirectory,
            Reasons ?? []);

    public static SandboxCommandRequest From(ResolvedCommand command) =>
        new(
            command.FileName,
            command.Arguments,
            command.DisplayCommand,
            command.WorkingDirectory,
            command.Reasons);
}

public sealed record SandboxRunRequest(
    ShellKind ShellKind,
    SandboxCommandRequest Command,
    string WorkspacePath,
    string Image,
    long MemoryLimitMb,
    double CpuLimit,
    int TimeoutSeconds);

public sealed record SandboxRunResponse(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string Command,
    string WorkingDirectory,
    DateTimeOffset Timestamp,
    string Runtime);
