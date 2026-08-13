using Nebula.Core.Agent;
using Nebula.Core.Operations;
using Nebula.Runner;

namespace Nebula.Agent.Application;

public sealed class DeterministicVerificationService(
    IWorkspaceStackDetector stackDetector,
    IShellExecutor executor,
    ILogger logger) : IDeterministicVerificationService
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LintTimeout = TimeSpan.FromSeconds(120);

    private const int MaxOutputLength = 3000;

    public async Task<DeterministicVerificationResult> VerifyAsync(
        string workingDirectory,
        IReadOnlyList<ExecutionEvidence> evidence,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!EvidenceTouchedCode(evidence))
            {
                return new DeterministicVerificationResult(
                    DeterministicVerificationVerdict.NotApplicable,
                    null,
                    null,
                    null,
                    "No code files were written or modified in this run.");
            }

            var stack = stackDetector.Detect(workingDirectory);
            if (stack.Kind == WorkspaceStackKind.Unknown)
            {
                return new DeterministicVerificationResult(
                    DeterministicVerificationVerdict.NotApplicable,
                    null,
                    null,
                    null,
                    "No known project stack detected; deterministic verification skipped.");
            }

            var command = SelectVerificationCommand(stack);
            if (string.IsNullOrWhiteSpace(command))
            {
                return new DeterministicVerificationResult(
                    DeterministicVerificationVerdict.NotApplicable,
                    stack.Kind.ToString(),
                    null,
                    null,
                    "No deterministic verification command available for the detected stack.");
            }

            var timeout = command.Contains("py_compile", StringComparison.OrdinalIgnoreCase)
                ? ParseTimeout
                : BuildTimeout;
            var result = await RunCommandAsync(command, workingDirectory, timeout, cancellationToken);

            var outputSummary = Truncate(result.CombinedOutput, MaxOutputLength);
            logger.Log(
                $"[DET_VERIFY] stack={stack.Kind}; command={command}; " +
                $"exitCode={result.ExitCode}; outputLength={result.CombinedOutput.Length}");

            if (!result.Success)
            {
                return new DeterministicVerificationResult(
                    DeterministicVerificationVerdict.Failed,
                    stack.Kind.ToString(),
                    command,
                    result.ExitCode,
                    outputSummary);
            }

            if (string.IsNullOrWhiteSpace(stack.LintCommand))
            {
                return new DeterministicVerificationResult(
                    DeterministicVerificationVerdict.Passed,
                    stack.Kind.ToString(),
                    command,
                    result.ExitCode,
                    outputSummary);
            }

            var lintResult = await RunCommandAsync(
                stack.LintCommand,
                workingDirectory,
                LintTimeout,
                cancellationToken);

            var lintOutput = Truncate(lintResult.CombinedOutput, MaxOutputLength);
            logger.Log(
                $"[DET_VERIFY] stack={stack.Kind}; lint={stack.LintCommand}; " +
                $"exitCode={lintResult.ExitCode}; outputLength={lintResult.CombinedOutput.Length}");

            return new DeterministicVerificationResult(
                lintResult.Success
                    ? DeterministicVerificationVerdict.Passed
                    : DeterministicVerificationVerdict.Failed,
                stack.Kind.ToString(),
                stack.LintCommand,
                lintResult.ExitCode,
                lintResult.Success
                    ? outputSummary
                    : $"Lint/format check failed ({stack.LintCommand}).\n\n{lintOutput}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeterministicVerificationResult(
                DeterministicVerificationVerdict.Error,
                null,
                null,
                null,
                "Deterministic verification timed out.");
        }
        catch (Exception ex)
        {
            logger.LogError($"[DET_VERIFY] Verification failed: {ex.Message}");
            return new DeterministicVerificationResult(
                DeterministicVerificationVerdict.Error,
                null,
                null,
                null,
                $"Deterministic verification could not run: {ex.Message}");
        }
    }

    private async Task<ShellCommandResult> RunCommandAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        if (executor is IDetailedShellExecutor detailedExecutor)
        {
            return await detailedExecutor.RunCommandDetailedAsync(
                command,
                workingDirectory,
                timeoutSource.Token);
        }

        var output = await executor.RunCommandAsync(command, timeoutSource.Token);
        return new ShellCommandResult
        {
            Command = command,
            WorkingDirectory = workingDirectory,
            StandardOutput = output,
            ExitCode = 0,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private static bool EvidenceTouchedCode(IReadOnlyList<ExecutionEvidence> evidence)
    {
        return evidence.Any(item =>
            item.OperationKind is OperationKind.FileWrite or OperationKind.ScriptContent &&
            item.Success);
    }

    private static string? SelectVerificationCommand(WorkspaceStack stack)
    {
        if (!string.IsNullOrWhiteSpace(stack.TestCommand))
        {
            return stack.TestCommand;
        }

        if (!string.IsNullOrWhiteSpace(stack.BuildCommand))
        {
            return stack.BuildCommand;
        }

        return stack.ParseCommand;
    }

    private static string Truncate(string value, int maxLength)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= maxLength
                ? value
                : value[..maxLength] + "...";
    }
}
