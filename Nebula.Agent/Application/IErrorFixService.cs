using Nebula.Core.Learning; // for ErrorFix record

namespace Nebula.Agent.Application;

public interface IErrorFixService
{
    Task<IReadOnlyList<ErrorFix>> SearchFixesAsync(
        string errorSignature,
        string failedCommand,
        string stack,
        string workspaceRoot,
        CancellationToken cancellationToken = default);
}