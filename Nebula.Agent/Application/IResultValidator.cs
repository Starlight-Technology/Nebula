using Nebula.Core.Operations;

namespace Nebula.Agent.Application;

public enum ValidationVerdict
{
    Sufficient,
    Insufficient,
    Mismatch
}

public sealed record ValidationResult(
    ValidationVerdict Verdict,
    string Reason,
    AgentToolAction? CorrectedAction = null);

public interface IResultValidator
{
    Task<ValidationResult> ValidateAsync(
        string objective,
        AgentToolAction action,
        string observation,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}