using Nebula.Agent.Data;
using Nebula.Core.Safety;
using Nebula.Llama.Client;

namespace Nebula.Agent.Application;

internal sealed record CommandValidation(
    bool Correct,
    CommandSafetyDecision SafetyDecision);

internal sealed class CommandValidationService(
    ILlamaClient llamaClient,
    ICommandPolicyEngine commandPolicyEngine)
{
    public async Task<CommandValidation> ValidateAsync(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        execution.IsCorrect = await VerifyCorrectnessAsync(execution, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var safetyDecision = await commandPolicyEngine.EvaluateAsync(execution.Run, cancellationToken);
        execution.IsSafe = safetyDecision.Decision == CommandSafetyDecisionType.Allow;
        execution.PassedLocalSafety = execution.IsSafe;
        execution.SafetyDecision = safetyDecision.Decision;
        execution.Notes = BuildVerificationNotes(execution, safetyDecision);

        return new CommandValidation(execution.IsCorrect, safetyDecision);
    }

    private async Task<bool> VerifyCorrectnessAsync(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        var response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". Does this command execute exactly the objective on {{PlatformDetector.GetCurrentOsType()}}?
            Objective: {{execution.Objective}}
            Command: {{execution.Run}}
            """,
            progress: null,
            cancellationToken);

        return AgentActionRunner.IsAffirmativeResponse(response);
    }

    internal static string BuildVerificationNotes(CommandExecution execution) =>
        execution.Notes ?? (execution.IsSafe
            ? "Aprovado pelo policy engine de comandos."
            : "Bloqueado pelo policy engine de comandos.");

    private static string BuildVerificationNotes(
        CommandExecution execution,
        CommandSafetyDecision safetyDecision)
    {
        var safetySummary =
            $"decision={safetyDecision.Decision}; intent={safetyDecision.Intent}; " +
            $"confidence={safetyDecision.Confidence:F3}; reasons={string.Join(" | ", safetyDecision.Reasons)}";

        if (!execution.IsCorrect)
        {
            return $"O modelo não confirmou que o comando atende ao objetivo; {safetySummary}";
        }

        return safetySummary;
    }
}
