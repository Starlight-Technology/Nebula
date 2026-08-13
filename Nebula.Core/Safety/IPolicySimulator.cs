using Nebula.Core.Operations;

namespace Nebula.Core.Safety;

/// <summary>
/// Explains how a command would be classified and decided by the safety
/// pipeline without executing it: intent, category, confidence, classifier
/// source, policy decision and whether approval would be skipped by the
/// current auto-approval settings.
/// </summary>
public interface IPolicySimulator
{
    Task<PolicySimulationResult> SimulateAsync(
        string text,
        string? rawCommand = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}

public sealed record PolicySimulationResult(
    string OriginalText,
    OperationKind OperationKind,
    CommandIntent Intent,
    string Category,
    double Confidence,
    string ClassificationSource,
    IReadOnlyList<string> ClassificationReasons,
    CommandSafetyDecisionType Decision,
    IReadOnlyList<string> DecisionReasons,
    string? ResolvedCommand,
    string? Shell,
    string? WorkingDirectory,
    string? ApprovalOutcome,
    string? ApprovalNote)
{
    public bool WouldRunWithoutApproval =>
        Decision == CommandSafetyDecisionType.Allow ||
        !string.IsNullOrWhiteSpace(ApprovalOutcome);
}