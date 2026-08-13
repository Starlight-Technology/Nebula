namespace Nebula.Core.Safety;

public enum CommandSafetyDecisionType
{
    Allow,
    AskApproval,
    Block
}

public sealed record CommandSafetyDecision(
    CommandSafetyDecisionType Decision,
    CommandIntent Intent,
    double Confidence,
    IReadOnlyList<string> Reasons);
