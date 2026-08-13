using Nebula.Core.Commands;

namespace Nebula.Runner;

public interface IShellOutputObserver
{
    void OnOutput(string chunk, bool isError);
}

public interface IStreamingShellExecutor : IResolvedCommandExecutor
{
    Task<ShellCommandResult> RunCommandDetailedAsync(
        ResolvedCommand command,
        IShellOutputObserver? observer,
        CancellationToken cancellationToken);
}