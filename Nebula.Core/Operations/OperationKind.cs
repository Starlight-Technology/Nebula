using Nebula.Core.Safety;

namespace Nebula.Core.Operations;

public enum OperationKind
{
    Chat,
    TerminalCommand,
    FileWrite,
    FileRead,
    ScriptContent,
    ScriptExecution,
    Research,
    Learning,
    ProjectScaffold,
    PlannedPatch,
    Unknown
}

public sealed class AgentStep
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid SessionId { get; init; }

    public string OriginalText { get; init; } = string.Empty;

    public string Objective { get; init; } = string.Empty;

    public OperationKind DeclaredKind { get; init; } = OperationKind.Unknown;

    public string? Command { get; init; }

    public string? Content { get; init; }

    public string? TargetPath { get; init; }

    public string? Language { get; init; }

    public string WorkingDirectory { get; init; } = string.Empty;
}

public interface IOperationKindDetector
{
    OperationKind Detect(AgentStep step);
}

public sealed record OperationPolicyRequest(
    Guid SessionId,
    Guid StepId,
    OperationKind OperationKind,
    string OriginalText,
    string? ResolvedCommand,
    string? TargetPath,
    CommandClassification Classification);

public interface IOperationPolicyEngine
{
    Task<CommandSafetyDecision> EvaluateAsync(
        OperationPolicyRequest request,
        CancellationToken cancellationToken = default);
}
