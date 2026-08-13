using Nebula.Core.Operations;
using Nebula.Core.Safety;

namespace Nebula.Services.Safety;

public sealed class OperationPolicyEngine(
    ICommandPolicyEngine commandPolicyEngine,
    Action<string>? log = null) : IOperationPolicyEngine
{
    public async Task<CommandSafetyDecision> EvaluateAsync(
        OperationPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        CommandSafetyDecision decision;
        if (request.OperationKind == OperationKind.ScriptExecution &&
            request.Classification.Intent is
                CommandIntent.Blocked or
                CommandIntent.DataExfiltration or
                CommandIntent.NeedsApproval)
        {
            decision = DecideSpecific(request.Classification);
        }
        else if (request.OperationKind is
                 OperationKind.TerminalCommand or
                 OperationKind.ScriptExecution)
        {
            decision = await commandPolicyEngine.EvaluateAsync(
                request.ResolvedCommand ?? string.Empty,
                cancellationToken);
        }
        else
        {
            decision = DecideSpecific(request.Classification);
        }

        log?.Invoke(
            $"Operation safety decision: sessionId={request.SessionId}; stepId={request.StepId}; " +
            $"operationKind={request.OperationKind}; intent={decision.Intent}; " +
            $"riskLevel={ToRiskLevel(decision)}; confidence={decision.Confidence:F3}; " +
            $"policyDecision={decision.Decision}; source={request.Classification.Source}; " +
            $"commandResolved={request.ResolvedCommand ?? "(none)"}; " +
            $"targetPath={request.TargetPath ?? "(none)"}; " +
            $"reasons={string.Join(" | ", decision.Reasons)}");
        return decision;
    }

    private static CommandSafetyDecision DecideSpecific(
        CommandClassification classification)
    {
        var decision = classification.Intent switch
        {
            CommandIntent.Blocked or CommandIntent.DataExfiltration =>
                CommandSafetyDecisionType.Block,
            CommandIntent.SafeReadOnly or
            CommandIntent.SafeWriteLocal or
            CommandIntent.SafeExecuteLocal
                when classification.Confidence >= 0.95 &&
                     IsDeterministic(classification.Source) =>
                CommandSafetyDecisionType.Allow,
            _ => CommandSafetyDecisionType.AskApproval
        };

        return new CommandSafetyDecision(
            decision,
            classification.Intent,
            classification.Confidence,
            [
                .. classification.Reasons,
                decision switch
                {
                    CommandSafetyDecisionType.Allow =>
                        "Operation-specific deterministic rules allowed the operation.",
                    CommandSafetyDecisionType.Block =>
                        "Operation-specific rules blocked the operation.",
                    _ =>
                        "The operation requires explicit approval."
                }
            ]);
    }

    private static bool IsDeterministic(string source) =>
        source.Contains("SafetyClassifier", StringComparison.Ordinal) ||
        source.Contains(
            nameof(DeterministicCommandClassifier),
            StringComparison.Ordinal);

    private static CommandRiskLevel ToRiskLevel(CommandSafetyDecision decision) =>
        decision.Decision switch
        {
            CommandSafetyDecisionType.Allow => CommandRiskLevel.Low,
            CommandSafetyDecisionType.AskApproval => CommandRiskLevel.Medium,
            _ => CommandRiskLevel.Critical
        };
}
