namespace Nebula.Core.Learning;

public enum OutputVerdict
{
    Match,
    Mismatch,
    Uncertain
}

public sealed record OutputVerification(
    OutputVerdict Verdict,
    string Reason,
    string? CorrectedCommand = null);

public interface IOutputVerificationService
{
    Task<OutputVerification> VerifyAsync(
        string objective,
        string command,
        string output,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
