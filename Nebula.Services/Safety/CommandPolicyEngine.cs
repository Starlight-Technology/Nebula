using Nebula.Core.Safety;

namespace Nebula.Services.Safety;

public sealed class CommandPolicyEngine(
    ICommandClassifier classifier,
    Action<string>? log = null) : ICommandPolicyEngine
{
    public async Task<CommandSafetyDecision> EvaluateAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        var classification = await classifier.ClassifyAsync(commandText, cancellationToken);
        var decision = Decide(classification);
        log?.Invoke(FormatLog(classification, decision));
        return decision;
    }

    internal static CommandSafetyDecision Decide(CommandClassification classification)
    {
        var decisionType = !IsDeterministic(classification)
            ? CommandSafetyDecisionType.AskApproval
            : classification.Intent switch
        {
            CommandIntent.Blocked or CommandIntent.DataExfiltration => CommandSafetyDecisionType.Block,
            CommandIntent.DestructiveOperation or
            CommandIntent.PackageInstall or
            CommandIntent.NetworkAccess or
            CommandIntent.PrivilegedOperation or
            CommandIntent.NeedsApproval or
            CommandIntent.Unknown => CommandSafetyDecisionType.AskApproval,
            CommandIntent.SafeReadOnly or
            CommandIntent.SafeWriteLocal or
            CommandIntent.SafeExecuteLocal when CanRulesAllow(classification) => CommandSafetyDecisionType.Allow,
            _ => CommandSafetyDecisionType.AskApproval
        };

        var reasons = new List<string>(classification.Reasons)
        {
            decisionType switch
            {
                CommandSafetyDecisionType.Allow => "Policy allowed a high-confidence deterministic safe intent.",
                CommandSafetyDecisionType.Block => "Policy blocked a prohibited or critical-risk intent.",
                _ => "Policy requires explicit approval before execution."
            }
        };

        return new CommandSafetyDecision(
            decisionType,
            classification.Intent,
            classification.Confidence,
            reasons);
    }

    private static bool CanRulesAllow(CommandClassification classification) =>
        classification.Confidence >= 0.95 && IsDeterministic(classification);

    private static bool IsDeterministic(CommandClassification classification) =>
        classification.Source.Contains(
            nameof(DeterministicCommandClassifier),
            StringComparison.Ordinal);

    private static string FormatLog(
        CommandClassification classification,
        CommandSafetyDecision decision) =>
        $"Command safety decision: decision={decision.Decision}; intent={decision.Intent}; " +
        $"confidence={decision.Confidence:F3}; source={classification.Source}; " +
        $"reasons={string.Join(" | ", decision.Reasons)}";
}
