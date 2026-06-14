namespace Nebula.Runner;

public interface IDetailedShellExecutor : IShellExecutor
{
    Task<ShellCommandResult> RunCommandDetailedAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken);
}
