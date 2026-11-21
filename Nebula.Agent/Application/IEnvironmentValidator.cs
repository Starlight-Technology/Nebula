using Nebula.Core.Operations;

namespace Nebula.Agent.Application;

public interface IEnvironmentValidator
{
    Task<EnvironmentValidation> ValidateAsync(
        OperationKind operationKind,
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}

public sealed record EnvironmentValidation(
    bool IsValid,
    IReadOnlyList<EnvironmentIssue> Issues);

public sealed record EnvironmentIssue(
    Severity Severity,
    string Message,
    string? SuggestedAction);

public enum Severity
{
    Warning,
    Error
}