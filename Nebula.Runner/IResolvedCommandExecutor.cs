using Nebula.Core.Commands;

namespace Nebula.Runner;

public interface IResolvedCommandExecutor : IDetailedShellExecutor
{
    Task<ShellCommandResult> RunCommandDetailedAsync(
        ResolvedCommand command,
        CancellationToken cancellationToken);
}
