namespace Nebula.Core.Operations;

public sealed record ExecutionEvidence(
    Guid Id,
    Guid SessionId,
    Guid StepId,
    OperationKind OperationKind,
    string? Command,
    string? FilePath,
    string? ContentHash,
    bool Executed,
    int? ExitCode,
    string? StdOut,
    string? StdErr,
    bool Success,
    DateTimeOffset CreatedAt);

public sealed record ExecutionEvidenceInput(
    Guid SessionId,
    Guid StepId,
    OperationKind OperationKind,
    string? Command = null,
    string? FilePath = null,
    string? Content = null,
    bool Executed = false,
    int? ExitCode = null,
    string? StdOut = null,
    string? StdErr = null,
    bool Success = false);

public interface IExecutionEvidenceCollector
{
    ExecutionEvidence Collect(ExecutionEvidenceInput input);
}
