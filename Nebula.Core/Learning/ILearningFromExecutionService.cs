using Nebula.Core.Operations;

namespace Nebula.Core.Learning;

public interface ILearningFromExecutionService
{
    Task RecordSuccessfulCommandAsync(
        string command,
        string resolvedCommand,
        string workingDirectory,
        int exitCode,
        string stdOut,
        string stdErr,
        Guid sessionId,
        Guid stepId,
        CancellationToken cancellationToken = default);

    Task RecordFailedCommandAsync(
        string command,
        string resolvedCommand,
        string workingDirectory,
        int? exitCode,
        string stdOut,
        string stdErr,
        string errorCategory,
        Guid sessionId,
        Guid stepId,
        CancellationToken cancellationToken = default);

    Task RecordSuccessfulFileOperationAsync(
        string operationKind,
        string filePath,
        string contentHash,
        Guid sessionId,
        Guid stepId,
        CancellationToken cancellationToken = default);
}
