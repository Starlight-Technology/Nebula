using Microsoft.EntityFrameworkCore;

using Nebula.Core.Learning;

using KnowledgeExperimentEntity =
    Nebula.Postgres.Context.Entities.KnowledgeExperiment;
using KnowledgeItemEntity =
    Nebula.Postgres.Context.Entities.KnowledgeItem;
using KnowledgeFactEntity =
    Nebula.Postgres.Context.Entities.KnowledgeFact;
using KnowledgeSourceEntity =
    Nebula.Postgres.Context.Entities.KnowledgeSource;

namespace Nebula.Postgres.Context;

public sealed class PostgresKnowledgeStore(PostgresContext context)
    : IKnowledgeRepository
{
    /// <summary>
    /// Saves a knowledge item and its source, fact, and experiment evidence.
    /// </summary>
    public async Task SaveAsync(
        KnowledgeItem item,
        IReadOnlyList<KnowledgeSource> sources,
        IReadOnlyList<KnowledgeFact> facts,
        KnowledgeExperiment experiment,
        CancellationToken cancellationToken = default)
    {
        var entity = Map(item);
        entity.Sources = sources.Select(Map).ToList();
        entity.Experiments = [Map(experiment)];
        entity.Facts = facts.Select(Map).ToList();

        var existing = await context.KnowledgeItems
            .Include(value => value.Sources)
            .Include(value => value.Experiments)
            .Include(value => value.Facts)
            .SingleOrDefaultAsync(
                value => value.Id == item.Id || value.Hash == item.Hash,
                cancellationToken);
        if (existing is null)
        {
            context.KnowledgeItems.Add(entity);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(entity);
            context.KnowledgeSources.RemoveRange(existing.Sources);
            context.KnowledgeExperiments.RemoveRange(existing.Experiments);
            context.KnowledgeFacts.RemoveRange(existing.Facts);
            context.KnowledgeSources.AddRange(entity.Sources);
            context.KnowledgeExperiments.AddRange(entity.Experiments);
            context.KnowledgeFacts.AddRange(entity.Facts);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Updates an existing experiment record (e.g., to add failure reason or retry count).
    /// </summary>
    public async Task UpdateExperimentAsync(
        Guid experimentId,
        KnowledgeExperiment updated,
        CancellationToken cancellationToken = default)
    {
        var existing = await context.KnowledgeExperiments
            .SingleOrDefaultAsync(
                value => value.Id == experimentId,
                cancellationToken);
        if (existing is null)
        {
            return;
        }

        if (updated.FailureReason is not null)
            existing.FailureReason = updated.FailureReason;
        if (updated.ErrorCategory is not null)
            existing.ErrorCategory = updated.ErrorCategory;
        if (updated.ResolvedCommand is not null)
            existing.ResolvedCommand = updated.ResolvedCommand;
        if (updated.EnvironmentFingerprint is not null)
            existing.EnvironmentFingerprint = updated.EnvironmentFingerprint;
        existing.RetryCount = updated.RetryCount;
        if (updated.OriginalExperimentId is not null)
            existing.OriginalExperimentId = updated.OriginalExperimentId;
        existing.Success = updated.Success;
        if (updated.StdOut is not null)
            existing.StdOut = updated.StdOut;
        if (updated.StdErr is not null)
            existing.StdErr = updated.StdErr;
        if (updated.ExitCode is not null)
            existing.ExitCode = updated.ExitCode;

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Finds trusted knowledge by domain and topic, excluding dangerous instructions.
    /// </summary>
    public async Task<IReadOnlyList<KnowledgeItem>> FindTrustedAsync(
        KnowledgeDomain domain,
        string topic,
        double minimumScore = 0.75,
        CancellationToken cancellationToken = default)
    {
        var normalizedTopic = topic.Trim();
        var items = await context.KnowledgeItems
            .AsNoTracking()
            .Where(item =>
                item.Domain == domain &&
                item.FinalScore >= minimumScore &&
                !item.IsDangerousInstruction &&
                (EF.Functions.ILike(item.Topic, $"%{normalizedTopic}%") ||
                 EF.Functions.ILike(item.Title, $"%{normalizedTopic}%")))
            .OrderByDescending(item => item.FinalScore)
            .ToListAsync(cancellationToken);

        return items.Select(Map).ToList();
    }

    /// <summary>
    /// Finds knowledge details with evidence and facts for diagnostics.
    /// </summary>
    public async Task<IReadOnlyList<KnowledgeLookupResult>> FindDetailsAsync(
        string topic,
        double minimumScore = 0.75,
        CancellationToken cancellationToken = default)
    {
        var normalizedTopic = topic.Trim();
        var items = await context.KnowledgeItems
            .AsNoTracking()
            .Include(item => item.Sources)
            .Include(item => item.Experiments)
            .Include(item => item.Facts)
            .Where(item =>
                item.FinalScore >= minimumScore &&
                (EF.Functions.ILike(item.Topic, $"%{normalizedTopic}%") ||
                 EF.Functions.ILike(item.Title, $"%{normalizedTopic}%") ||
                 EF.Functions.ILike(item.Content, $"%{normalizedTopic}%") ||
                 EF.Functions.ILike(item.Summary, $"%{normalizedTopic}%")))
            .OrderByDescending(item => item.FinalScore)
            .Take(20)
            .ToListAsync(cancellationToken);

        return items.Select(item => new KnowledgeLookupResult(
            Map(item),
            item.Sources.Select(Map).ToList(),
            item.Experiments.Select(Map).ToList(),
            item.Facts.Select(Map).ToList())).ToList();
    }

    /// <summary>
    /// Finds a stored knowledge item by its deterministic hash.
    /// </summary>
    public async Task<KnowledgeLookupResult?> FindByHashAsync(
        string hash,
        CancellationToken cancellationToken = default)
    {
        var item = await context.KnowledgeItems
            .AsNoTracking()
            .Include(value => value.Sources)
            .Include(value => value.Experiments)
            .Include(value => value.Facts)
            .SingleOrDefaultAsync(value => value.Hash == hash, cancellationToken);

        return item is null
            ? null
            : new KnowledgeLookupResult(
                Map(item),
                item.Sources.Select(Map).ToList(),
                item.Experiments.Select(Map).ToList(),
                item.Facts.Select(Map).ToList());
    }

    private static KnowledgeItemEntity Map(KnowledgeItem item) =>
        new()
        {
            Id = item.Id,
            Domain = item.Domain,
            Kind = item.Kind,
            Topic = item.Topic,
            Title = item.Title,
            Content = item.Content,
            Summary = item.Summary,
            Examples = item.Examples,
            Warnings = item.Warnings,
            Tags = item.Tags,
            NormalizedCommand = item.NormalizedCommand,
            Language = item.Language,
            OS = item.OS,
            Shell = item.Shell,
            SourceUrl = item.SourceUrl,
            SourceType = item.SourceType,
            SourceName = item.SourceName,
            RiskLevel = item.RiskLevel,
            ConfidenceScore = item.ConfidenceScore,
            SourceScore = item.SourceScore,
            ClassificationConfidence = item.ClassificationConfidence,
            SafetyScore = item.SafetyScore,
            VerificationScore = item.VerificationScore,
            FinalScore = item.FinalScore,
            Hash = item.Hash,
            LastSeenAt = item.LastSeenAt,
            ObservationCount = item.ObservationCount,
            IsExecutableAdvice = item.IsExecutableAdvice,
            IsDangerousInstruction = item.IsDangerousInstruction,
            IsValidated = item.IsValidated,
            ValidationNotes = item.ValidationNotes,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };

    private static KnowledgeItem Map(KnowledgeItemEntity item) =>
        new()
        {
            Id = item.Id,
            Domain = item.Domain,
            Kind = item.Kind,
            Topic = item.Topic,
            Title = item.Title,
            Content = item.Content,
            Summary = item.Summary,
            Examples = item.Examples,
            Warnings = item.Warnings,
            Tags = item.Tags,
            NormalizedCommand = item.NormalizedCommand,
            Language = item.Language,
            OS = item.OS,
            Shell = item.Shell,
            SourceUrl = item.SourceUrl,
            SourceType = item.SourceType,
            SourceName = item.SourceName,
            RiskLevel = item.RiskLevel,
            ConfidenceScore = item.ConfidenceScore,
            SourceScore = item.SourceScore,
            ClassificationConfidence = item.ClassificationConfidence,
            SafetyScore = item.SafetyScore,
            VerificationScore = item.VerificationScore,
            FinalScore = item.FinalScore,
            Hash = item.Hash,
            LastSeenAt = item.LastSeenAt,
            ObservationCount = item.ObservationCount,
            IsExecutableAdvice = item.IsExecutableAdvice,
            IsDangerousInstruction = item.IsDangerousInstruction,
            IsValidated = item.IsValidated,
            ValidationNotes = item.ValidationNotes,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };

    private static KnowledgeSource Map(KnowledgeSourceEntity source) =>
        new()
        {
            Id = source.Id,
            KnowledgeItemId = source.KnowledgeItemId,
            Url = source.Url,
            Title = source.Title,
            Publisher = source.Publisher,
            ProviderName = source.ProviderName,
            SourceType = source.SourceType,
            ExtractedContent = source.ExtractedContent,
            PublishedAt = source.PublishedAt,
            RetrievedAt = source.RetrievedAt,
            TrustScore = source.TrustScore
        };

    private static KnowledgeExperiment Map(
        KnowledgeExperimentEntity experiment) =>
        new()
        {
            Id = experiment.Id,
            KnowledgeItemId = experiment.KnowledgeItemId,
            VerificationKind = experiment.VerificationKind,
            CommandExecuted = experiment.CommandExecuted,
            TestCode = experiment.TestCode,
            ExitCode = experiment.ExitCode,
            StdOut = experiment.StdOut,
            StdErr = experiment.StdErr,
            Success = experiment.Success,
            FailureReason = experiment.FailureReason,
            ErrorCategory = experiment.ErrorCategory,
            ResolvedCommand = experiment.ResolvedCommand,
            EnvironmentFingerprint = experiment.EnvironmentFingerprint,
            RetryCount = experiment.RetryCount,
            OriginalExperimentId = experiment.OriginalExperimentId,
            EvidenceHash = experiment.EvidenceHash,
            CreatedAt = experiment.CreatedAt
        };

    private static KnowledgeFact Map(KnowledgeFactEntity fact) =>
        new()
        {
            Id = fact.Id,
            KnowledgeItemId = fact.KnowledgeItemId,
            Fact = fact.Fact,
            Confidence = fact.Confidence,
            SourceUrl = fact.SourceUrl
        };

    private static KnowledgeSourceEntity Map(KnowledgeSource source) =>
        new()
        {
            Id = source.Id,
            KnowledgeItemId = source.KnowledgeItemId,
            Url = source.Url,
            Title = source.Title,
            Publisher = source.Publisher,
            ProviderName = source.ProviderName,
            SourceType = source.SourceType,
            ExtractedContent = source.ExtractedContent,
            PublishedAt = source.PublishedAt,
            RetrievedAt = source.RetrievedAt,
            TrustScore = source.TrustScore
        };

    private static KnowledgeFactEntity Map(KnowledgeFact fact) =>
        new()
        {
            Id = fact.Id,
            KnowledgeItemId = fact.KnowledgeItemId,
            Fact = fact.Fact,
            Confidence = fact.Confidence,
            SourceUrl = fact.SourceUrl
        };

    private static KnowledgeExperimentEntity Map(
        KnowledgeExperiment experiment) =>
        new()
        {
            Id = experiment.Id,
            KnowledgeItemId = experiment.KnowledgeItemId,
            VerificationKind = experiment.VerificationKind,
            CommandExecuted = experiment.CommandExecuted,
            TestCode = experiment.TestCode,
            ExitCode = experiment.ExitCode,
            StdOut = experiment.StdOut,
            StdErr = experiment.StdErr,
            Success = experiment.Success,
            FailureReason = experiment.FailureReason,
            ErrorCategory = experiment.ErrorCategory,
            ResolvedCommand = experiment.ResolvedCommand,
            EnvironmentFingerprint = experiment.EnvironmentFingerprint,
            RetryCount = experiment.RetryCount,
            OriginalExperimentId = experiment.OriginalExperimentId,
            EvidenceHash = experiment.EvidenceHash,
            CreatedAt = experiment.CreatedAt
        };
}
