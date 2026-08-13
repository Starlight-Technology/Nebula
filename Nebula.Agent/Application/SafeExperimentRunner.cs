using System.Security.Cryptography;
using System.Text;

using Nebula.Core.Commands;
using Nebula.Core.Learning;
using Nebula.Core.Safety;
using Nebula.Runner;

namespace Nebula.Agent.Application;

public sealed class SafeExperimentRunner(
    IShellExecutor executor,
    ICommandPolicyEngine commandPolicyEngine,
    ICommandIntentParser commandIntentParser,
    ICommandResolver commandResolver,
    IRuntimeCommandEnvironmentDetector environmentDetector,
    IScriptContentSafetyClassifier scriptClassifier)
    : ISafeExperimentRunner
{
    public async Task<KnowledgeExperiment> TryVerifyAsync(
        KnowledgeItem item,
        CancellationToken cancellationToken)
    {
        if (item.Domain is
            KnowledgeDomain.Physics or KnowledgeDomain.Chemistry)
        {
            return NotTestable(item);
        }

        if (item.Kind == KnowledgeItemKind.Command &&
            !string.IsNullOrWhiteSpace(item.NormalizedCommand))
        {
            return await VerifyCommandAsync(item, cancellationToken);
        }

        if (item.Kind == KnowledgeItemKind.CodeSnippet &&
            item.Domain == KnowledgeDomain.Python)
        {
            return await VerifyPythonAsync(item, cancellationToken);
        }

        return new KnowledgeExperiment
        {
            KnowledgeItemId = item.Id,
            VerificationKind = item.Kind == KnowledgeItemKind.Formula
                ? VerificationKind.NumericCheck
                : VerificationKind.StaticAnalysis,
            TestCode = item.Content,
            Success = item.Kind != KnowledgeItemKind.Formula,
            EvidenceHash = Hash(item.Content)
        };
    }

    private async Task<KnowledgeExperiment> VerifyCommandAsync(
        KnowledgeItem item,
        CancellationToken cancellationToken)
    {
        var tempDirectory = CreateControlledTempDirectory();
        var environment = environmentDetector.Detect(tempDirectory);
        var request = commandIntentParser.Parse(
            item.Topic,
            item.NormalizedCommand,
            tempDirectory);
        var resolved = commandResolver.Resolve(request, environment);
        var safety = await commandPolicyEngine.EvaluateAsync(
            resolved.DisplayCommand,
            cancellationToken);
        if (safety.Decision != CommandSafetyDecisionType.Allow)
        {
            return new KnowledgeExperiment
            {
                KnowledgeItemId = item.Id,
                VerificationKind = VerificationKind.StaticAnalysis,
                CommandExecuted = resolved.DisplayCommand,
                ResolvedCommand = resolved.DisplayCommand,
                Success = false,
                FailureReason = $"Policy returned {safety.Decision}",
                ErrorCategory = "PolicyBlocked",
                StdErr =
                    $"Experiment was not executed because policy returned {safety.Decision}.",
                EvidenceHash = Hash(
                    $"{resolved.DisplayCommand}|{safety.Decision}")
            };
        }

        var result = await ExecuteAsync(resolved, cancellationToken);
        return FromResult(
            item,
            VerificationKind.SafeExecution,
            result);
    }

    private async Task<KnowledgeExperiment> VerifyPythonAsync(
        KnowledgeItem item,
        CancellationToken cancellationToken)
    {
        var tempDirectory = CreateControlledTempDirectory();
        var scriptPath = Path.Combine(tempDirectory, "experiment.py");
        var classification = scriptClassifier.Classify(
            item.Content,
            "python",
            scriptPath);
        if (classification.Intent != CommandIntent.SafeWriteLocal ||
            classification.Confidence < 0.95)
        {
            return new KnowledgeExperiment
            {
                KnowledgeItemId = item.Id,
                VerificationKind = VerificationKind.StaticAnalysis,
                TestCode = item.Content,
                Success = false,
                FailureReason = string.Join(" | ", classification.Reasons),
                ErrorCategory = "ScriptClassificationFailed",
                StdErr = string.Join(" | ", classification.Reasons),
                EvidenceHash = Hash(item.Content)
            };
        }

        await File.WriteAllTextAsync(
            scriptPath,
            item.Content,
            Encoding.UTF8,
            cancellationToken);
        var environment = environmentDetector.Detect(tempDirectory);
        var command = $"python \"{scriptPath}\"";
        var request = commandIntentParser.Parse(
            item.Topic,
            command,
            tempDirectory);
        var resolved = commandResolver.Resolve(request, environment);
        var safety = await commandPolicyEngine.EvaluateAsync(
            resolved.DisplayCommand,
            cancellationToken);
        if (safety.Decision != CommandSafetyDecisionType.Allow)
        {
            return new KnowledgeExperiment
            {
                KnowledgeItemId = item.Id,
                VerificationKind = VerificationKind.StaticAnalysis,
                CommandExecuted = resolved.DisplayCommand,
                ResolvedCommand = resolved.DisplayCommand,
                TestCode = item.Content,
                Success = false,
                FailureReason = $"Policy returned {safety.Decision}",
                ErrorCategory = "PolicyBlocked",
                StdErr =
                    $"Experiment was not executed because policy returned {safety.Decision}.",
                EvidenceHash = Hash(item.Content)
            };
        }

        var result = await ExecuteAsync(resolved, cancellationToken);
        var experiment = FromResult(
            item,
            VerificationKind.SafeExecution,
            result);
        experiment.TestCode = item.Content;
        return experiment;
    }

    private async Task<ShellCommandResult> ExecuteAsync(
        ResolvedCommand resolved,
        CancellationToken cancellationToken)
    {
        if (executor is IResolvedCommandExecutor resolvedExecutor)
        {
            return await resolvedExecutor.RunCommandDetailedAsync(
                resolved,
                cancellationToken);
        }

        if (executor is IDetailedShellExecutor detailedExecutor)
        {
            return await detailedExecutor.RunCommandDetailedAsync(
                resolved.DisplayCommand,
                resolved.WorkingDirectory,
                cancellationToken);
        }

        var output = await executor.RunCommandAsync(
            resolved.DisplayCommand,
            cancellationToken);
        return new ShellCommandResult
        {
            Command = resolved.DisplayCommand,
            WorkingDirectory = resolved.WorkingDirectory,
            StandardOutput = output,
            ExitCode = 0
        };
    }

    private static KnowledgeExperiment FromResult(
        KnowledgeItem item,
        VerificationKind verificationKind,
        ShellCommandResult result) =>
        new()
        {
            KnowledgeItemId = item.Id,
            VerificationKind = verificationKind,
            CommandExecuted = result.Command,
            ResolvedCommand = result.Command,
            ExitCode = result.ExitCode,
            StdOut = result.StandardOutput,
            StdErr = result.StandardError,
            Success = result.Success,
            FailureReason = result.Success ? null : $"Exit code: {result.ExitCode}",
            ErrorCategory = result.Success ? null : CategorizeError(result),
            EvidenceHash = Hash(
                $"{result.Command}|{result.ExitCode}|{result.StandardOutput}|{result.StandardError}")
        };

    private static string CategorizeError(ShellCommandResult result)
    {
        var stderr = (result.StandardError ?? "").ToLowerInvariant();
        var stdout = (result.StandardOutput ?? "").ToLowerInvariant();
        var combined = $"{stderr} {stdout}";

        if (combined.Contains("not found") || combined.Contains("not recognized") ||
            combined.Contains("no such file") || combined.Contains("could not find") ||
            combined.Contains("não encontrado") || combined.Contains("não é reconhecido"))
            return "CommandNotFound";

        if (combined.Contains("permission denied") || combined.Contains("access denied") ||
            combined.Contains("permissão negada") || combined.Contains("acesso negado"))
            return "PermissionDenied";

        if (combined.Contains("syntax error") || combined.Contains("erro de sintaxe") ||
            combined.Contains("unexpected token") || (result.ExitCode == -1 && string.IsNullOrWhiteSpace(combined)))
            return "SyntaxError";

        if (combined.Contains("timed out") || combined.Contains("timeout") ||
            combined.Contains("tempo esgotado"))
            return "Timeout";

        if (combined.Contains("network") || combined.Contains("connection") ||
            combined.Contains("internet") || combined.Contains("dns") ||
            combined.Contains("conexão") || combined.Contains("rede"))
            return "NetworkError";

        return "Other";
    }

    private static KnowledgeExperiment NotTestable(KnowledgeItem item) =>
        new()
        {
            KnowledgeItemId = item.Id,
            VerificationKind = VerificationKind.NotTestableLocally,
            Success = false,
            FailureReason = "Cannot verify physics/chemistry locally",
            ErrorCategory = "NotTestableLocally",
            EvidenceHash = Hash(
                $"{item.Domain}|{item.Kind}|not-testable-locally")
        };

    private static string CreateControlledTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Nebula",
            "experiments",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
