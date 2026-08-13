using Nebula.Core.Learning;
using Nebula.Services.Learning;

namespace Nebula.Agent.Application;

public sealed class LearningFromExecutionService(
    IKnowledgeStore knowledgeStore,
    IKnowledgeScoreEngine scoreEngine,
    ILogger logger)
    : ILearningFromExecutionService
{
    public async Task RecordSuccessfulCommandAsync(
        string command,
        string resolvedCommand,
        string workingDirectory,
        int exitCode,
        string stdOut,
        string stdErr,
        Guid sessionId,
        Guid stepId,
        CancellationToken cancellationToken = default)
    {
        await RecordCommandAsync(
            command,
            resolvedCommand,
            workingDirectory,
            exitCode,
            stdOut,
            stdErr,
            success: true,
            errorCategory: null,
            sessionId,
            stepId,
            cancellationToken);
    }

    public async Task RecordFailedCommandAsync(
        string command,
        string resolvedCommand,
        string workingDirectory,
        int? exitCode,
        string stdOut,
        string stdErr,
        string errorCategory,
        Guid sessionId,
        Guid stepId,
        CancellationToken cancellationToken = default)
    {
        await RecordCommandAsync(
            command,
            resolvedCommand,
            workingDirectory,
            exitCode ?? -1,
            stdOut,
            stdErr,
            success: false,
            errorCategory,
            sessionId,
            stepId,
            cancellationToken);
    }

    public async Task RecordSuccessfulFileOperationAsync(
        string operationKind,
        string filePath,
        string contentHash,
        Guid sessionId,
        Guid stepId,
        CancellationToken cancellationToken = default)
    {
        var topic = $"File {operationKind}: {Path.GetFileName(filePath)}";
        var content = $"Operation: {operationKind}\nPath: {filePath}\nContentHash: {contentHash}";
        var hash = KnowledgeHash.Create(
            Nebula.Core.Learning.KnowledgeDomain.General,
            topic,
            content,
            content);

        var existing = await FindByHashAsync(hash, cancellationToken);
        if (existing is not null)
        {
            var item = existing.Item;
            item.LastSeenAt = DateTimeOffset.UtcNow;
            item.ObservationCount++;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            var existExperiment = new KnowledgeExperiment
            {
                KnowledgeItemId = item.Id,
                VerificationKind = VerificationKind.SafeExecution,
                CommandExecuted = filePath,
                ResolvedCommand = filePath,
                ExitCode = 0,
                StdOut = $"File operation recorded: {topic}",
                Success = true,
                FailureReason = null,
                ErrorCategory = null,
                EvidenceHash = contentHash
            };
            item.FinalScore = scoreEngine.Calculate(item);
            await knowledgeStore.SaveAsync(item, [], [], existExperiment, cancellationToken);
            return;
        }

        var newItem = new KnowledgeItem
        {
            Domain = KnowledgeDomain.General,
            Kind = KnowledgeItemKind.Procedure,
            Topic = topic,
            Title = topic,
            Content = content,
            Summary = $"Agent successfully performed {operationKind} on {Path.GetFileName(filePath)}",
            Tags = "file-operation,auto-learned",
            NormalizedCommand = null,
            SourceUrl = $"session://{sessionId}/step-{stepId}",
            SourceType = LearningSourceType.ExistingKnowledgeBase,
            SourceName = "LearningFromExecutionService",
            RiskLevel = KnowledgeRiskLevel.Safe,
            ConfidenceScore = 0.90,
            SourceScore = 0.90,
            ClassificationConfidence = 0.90,
            SafetyScore = 1.0,
            VerificationScore = 0.85,
            Hash = hash,
            IsExecutableAdvice = false,
            IsDangerousInstruction = false,
            IsValidated = true,
            ValidationNotes = "Learned from successful agent execution.",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        newItem.FinalScore = scoreEngine.Calculate(newItem);

        var createExperiment = new KnowledgeExperiment
        {
            KnowledgeItemId = newItem.Id,
            VerificationKind = VerificationKind.SafeExecution,
            CommandExecuted = filePath,
            ResolvedCommand = filePath,
            ExitCode = 0,
            StdOut = $"File operation recorded: {topic}",
            Success = true,
            EvidenceHash = contentHash
        };

        await knowledgeStore.SaveAsync(newItem, [], [], createExperiment, cancellationToken);
        logger.Log($"[LEARN] Recorded successful file operation: {topic} (hash={hash[..8]}...)");
    }

    private async Task RecordCommandAsync(
        string command,
        string resolvedCommand,
        string workingDirectory,
        int exitCode,
        string stdOut,
        string stdErr,
        bool success,
        string? errorCategory,
        Guid sessionId,
        Guid stepId,
        CancellationToken cancellationToken)
    {
        var content = $"Command: {command}\nResolved: {resolvedCommand}\nDirectory: {workingDirectory}\nExitCode: {exitCode}";
        var hash = KnowledgeHash.Create(
            KnowledgeDomain.General,
            command,
            content,
            content);

        var existing = await FindByHashAsync(hash, cancellationToken);
        if (existing is not null)
        {
            var item = existing.Item;
            item.LastSeenAt = DateTimeOffset.UtcNow;
            if (success)
            {
                item.ObservationCount++;
                item.VerificationScore = Math.Min(1.0, item.VerificationScore + 0.05);
            }
            else
            {
                item.VerificationScore = Math.Max(0, item.VerificationScore - 0.15);
                item.SafetyScore = Math.Max(0, item.SafetyScore - 0.10);
            }
            item.UpdatedAt = DateTimeOffset.UtcNow;
            item.FinalScore = scoreEngine.Calculate(item);

            var updateExperiment = new KnowledgeExperiment
            {
                KnowledgeItemId = item.Id,
                VerificationKind = VerificationKind.SafeExecution,
                CommandExecuted = command,
                ResolvedCommand = resolvedCommand,
                ExitCode = exitCode,
                StdOut = Truncate(stdOut, 2000),
                StdErr = Truncate(stdErr, 2000),
                Success = success,
                FailureReason = success ? null : $"Exit code: {exitCode}",
                ErrorCategory = errorCategory,
                EvidenceHash = hash
            };

            await knowledgeStore.SaveAsync(item, [], [], updateExperiment, cancellationToken);
            logger.Log($"[LEARN] Updated command record: {command} (success={success}, observations={item.ObservationCount})");
            return;
        }

        var kind = success ? KnowledgeItemKind.Command : KnowledgeItemKind.Warning;
        var title = success
            ? $"Command: {command}"
            : $"Failed command: {command}";
        var summary = success
            ? $"Agent successfully executed '{command}' with exit code {exitCode}"
            : $"Agent failed to execute '{command}' with exit code {exitCode}: {errorCategory}";

        var newItem = new KnowledgeItem
        {
            Domain = KnowledgeDomain.General,
            Kind = kind,
            Topic = command,
            Title = title,
            Content = content,
            Summary = summary,
            Tags = success ? "command,auto-learned,success" : "command,auto-learned,failure,warning",
            NormalizedCommand = resolvedCommand,
            Language = "shell",
            OS = Environment.OSVersion.Platform.ToString(),
            Shell = "powershell",
            SourceUrl = $"session://{sessionId}/step-{stepId}",
            SourceType = LearningSourceType.ExistingKnowledgeBase,
            SourceName = "LearningFromExecutionService",
            RiskLevel = success ? KnowledgeRiskLevel.Safe : KnowledgeRiskLevel.LowRisk,
            ConfidenceScore = 0.85,
            SourceScore = 0.90,
            ClassificationConfidence = 0.85,
            SafetyScore = success ? 1.0 : 0.70,
            VerificationScore = success ? 0.85 : 0.25,
            Hash = hash,
            IsExecutableAdvice = success,
            IsDangerousInstruction = false,
            IsValidated = success,
            ValidationNotes = success
                ? "Learned from successful agent execution."
                : "Learned from failed agent execution - verify before reusing.",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        newItem.FinalScore = scoreEngine.Calculate(newItem);

        var commandExperiment = new KnowledgeExperiment
        {
            KnowledgeItemId = newItem.Id,
            VerificationKind = VerificationKind.SafeExecution,
            CommandExecuted = command,
            ResolvedCommand = resolvedCommand,
            ExitCode = exitCode,
            StdOut = Truncate(stdOut, 2000),
            StdErr = Truncate(stdErr, 2000),
            Success = success,
            FailureReason = success ? null : $"Exit code: {exitCode}",
            ErrorCategory = errorCategory,
            EvidenceHash = hash
        };

        await knowledgeStore.SaveAsync(newItem, [], [], commandExperiment, cancellationToken);
        logger.Log($"[LEARN] Recorded command: {command} (success={success}, hash={hash[..8]}...)");
    }

    private async Task<KnowledgeLookupResult?> FindByHashAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        if (knowledgeStore is IKnowledgeRepository repository)
        {
            return await repository.FindByHashAsync(hash, cancellationToken);
        }
        var details = await knowledgeStore.FindDetailsAsync(hash, minimumScore: 0, cancellationToken);
        return details.FirstOrDefault(r => r.Item.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private static string Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty :
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
