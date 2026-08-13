using Nebula.Core.Operations;

namespace Nebula.Core.Agent;

public enum WorkspaceStackKind
{
    Unknown,
    DotNet,
    Node,
    Python
}

public sealed record WorkspaceStack(
    WorkspaceStackKind Kind,
    string? ProjectFile,
    string? BuildCommand,
    string? TestCommand,
    string? ParseCommand,
    string? LintCommand = null);

public interface IWorkspaceStackDetector
{
    WorkspaceStack Detect(string workingDirectory);
}

public enum DeterministicVerificationVerdict
{
    NotApplicable,
    Passed,
    Failed,
    Error
}

public sealed record DeterministicVerificationResult(
    DeterministicVerificationVerdict Verdict,
    string? Tool,
    string? Command,
    int? ExitCode,
    string? Output);

public interface IDeterministicVerificationService
{
    Task<DeterministicVerificationResult> VerifyAsync(
        string workingDirectory,
        IReadOnlyList<ExecutionEvidence> evidence,
        CancellationToken cancellationToken = default);
}
