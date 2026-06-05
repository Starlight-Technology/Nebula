namespace Nebula.Runner;

public interface IShellExecutor
{
    Task<string> RunCommandAsync(string command);

    Task<string> RunCommandAsync(string command, CancellationToken cancellationToken);
}
