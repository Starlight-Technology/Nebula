using Nebula.Agent.Data;
using Nebula.Llama.Client;

namespace Nebula.Agent.Application;

internal sealed record CommandValidation(bool Correct, bool Safe);

internal sealed class CommandValidationService(ILlamaClient llamaClient)
{
    public async Task<CommandValidation> ValidateAsync(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        execution.IsCorrect = await VerifyCorrectnessAsync(execution, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        execution.IsSafe = await VerifySafetyAsync(execution, cancellationToken);
        execution.PassedLocalSafety = PlatformDetector.IsCommandContentSafe(execution.Run);
        execution.Notes = BuildVerificationNotes(execution);

        return new CommandValidation(
            execution.IsCorrect,
            execution.IsSafe && execution.PassedLocalSafety);
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

    private async Task<bool> VerifySafetyAsync(
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        var response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". Is this command safe and allowed to execute on {{PlatformDetector.GetCurrentOsType()}}?
            Command: {{execution.Run}}
            """,
            progress: null,
            cancellationToken);

        return AgentActionRunner.IsAffirmativeResponse(response);
    }

    internal static string BuildVerificationNotes(CommandExecution execution)
    {
        if (execution.IsCorrect && execution.IsSafe && execution.PassedLocalSafety)
        {
            return "Aprovado pela verificacao do modelo e pela protecao local.";
        }

        var failures = new List<string>();
        AddFailure(
            failures,
            !execution.IsCorrect,
            "o modelo nao confirmou que o comando atende ao objetivo");
        AddFailure(
            failures,
            !execution.IsSafe,
            "o modelo nao considerou o comando seguro");
        AddFailure(
            failures,
            !execution.PassedLocalSafety,
            "a protecao local bloqueou um padrao de comando perigoso");

        return $"Passo bloqueado porque {string.Join("; ", failures)}.";
    }

    private static void AddFailure(
        List<string> failures,
        bool condition,
        string message)
    {
        if (condition)
        {
            failures.Add(message);
        }
    }
}
