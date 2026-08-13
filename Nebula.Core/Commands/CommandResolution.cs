namespace Nebula.Core.Commands;

public enum OperatingSystemKind
{
    Windows,
    Linux,
    MacOS,
    Unknown
}

public enum ShellKind
{
    PowerShell,
    Cmd,
    Bash,
    Sh,
    Unknown
}

public sealed record RuntimeCommandEnvironment(
    OperatingSystemKind OS,
    ShellKind Shell,
    string WorkingDirectory);

public sealed record CommandRequest(
    string UserText,
    string? RequestedPath,
    string? RequestedDrive,
    string? RawCommand);

public sealed record ResolvedCommand(
    string FileName,
    string Arguments,
    string DisplayCommand,
    string WorkingDirectory,
    IReadOnlyList<string> Reasons);

public interface ICommandIntentParser
{
    CommandRequest Parse(
        string userText,
        string? rawCommand,
        string workingDirectory);
}

public interface ICommandResolver
{
    ResolvedCommand Resolve(
        CommandRequest request,
        RuntimeCommandEnvironment environment);
}

public interface IRuntimeCommandEnvironmentDetector
{
    RuntimeCommandEnvironment Detect(string workingDirectory);
}
