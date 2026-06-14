using System.Text.Json;
using System.Diagnostics;

using Nebula.Core.Learning;
using Nebula.Core.Safety;

namespace Nebula.Agent.Application;

public sealed class LearningEngine(
    IWebResearchService researchService,
    IKnowledgeExtractor extractor,
    IKnowledgeClassifier classifier,
    IKnowledgeStore store,
    ISafeExperimentRunner experimentRunner,
    IKnowledgeScoreEngine scoreEngine,
    ILogger logger) : ILearningEngine
{
    public async Task<LearningReport> LearnAsync(
        LearningRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var research = await researchService.SearchAsync(
                request.Topic,
                request.Domain,
                cancellationToken);
            if (research.Count == 0)
            {
                logger.LogError(
                    $"[AGENT] Learning stopped because no real sources were retrieved for '{request.Topic}'.");
                return new LearningReport(
                    false,
                    "Nenhuma fonte real foi encontrada ou baixada. Nenhum conhecimento foi criado.",
                    [],
                    [],
                    []);
            }

            var drafts = await extractor.ExtractAsync(
                request.Topic,
                request.Domain,
                research,
                cancellationToken);
            var items = new List<KnowledgeItem>();
            var sources = new List<KnowledgeSource>();
            var experiments = new List<KnowledgeExperiment>();
            var facts = new List<KnowledgeFact>();

            foreach (var draft in drafts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = research.SingleOrDefault(value =>
                    value.Url.Equals(
                        draft.SourceUrl,
                        StringComparison.OrdinalIgnoreCase));
                if (source is null)
                {
                    logger.LogError(
                        $"[AGENT] Learning item rejected because sourceUrl '{draft.SourceUrl}' was not returned by research.");
                    continue;
                }

                var classificationTimer = Stopwatch.StartNew();
                var classification = await classifier.ClassifyAsync(
                    draft,
                    cancellationToken);
                logger.Log(
                    $"[AGENT] Knowledge classification: topic={request.Topic}; " +
                    $"elapsedMs={classificationTimer.ElapsedMilliseconds}; " +
                    $"domain={classification.Domain}; kind={classification.Kind}; " +
                    $"confidence={classification.Confidence:F3}");
                var item = CreateItem(
                    request.Topic,
                    draft,
                    source,
                    classification);
                var experiment = await experimentRunner.TryVerifyAsync(
                    item,
                    cancellationToken);
                var itemFacts = CreateFacts(item, draft);
                ApplyVerificationScore(item, experiment);
                item.FinalScore = scoreEngine.Calculate(item);
                item.UpdatedAt = DateTimeOffset.UtcNow;

                var knowledgeSource = new KnowledgeSource
                {
                    KnowledgeItemId = item.Id,
                    Url = source.Url,
                    Title = source.Title,
                    Publisher = source.Publisher ?? string.Empty,
                    ExtractedContent = source.Snippet,
                    PublishedAt = null,
                    RetrievedAt = source.RetrievedAt,
                    TrustScore = source.SourceScore
                };
                experiment.KnowledgeItemId = item.Id;

                await store.SaveAsync(
                    item,
                    [knowledgeSource],
                    itemFacts,
                    experiment,
                    cancellationToken);
                items.Add(item);
                sources.Add(knowledgeSource);
                experiments.Add(experiment);
                facts.AddRange(itemFacts);

                logger.Log(
                    $"[AGENT] Learning item stored: knowledgeItemId={item.Id}; " +
                    $"domain={item.Domain}; kind={item.Kind}; finalScore={item.FinalScore:F3}; " +
                    $"verification={experiment.VerificationKind}; evidenceId={experiment.Id}");
            }

            return new LearningReport(
                true,
                null,
                items,
                sources,
                experiments,
                facts);
        }
        catch (InvalidOperationException ex)
            when (IsMissingConfigurationError(ex.Message))
        {
            logger.LogError($"[AGENT] {ex.Message}");
            return new LearningReport(
                false,
                "Pesquisa web não configurada. Configure WebResearch:Provider e WebResearch:ApiKey.",
                [],
                [],
                []);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError($"[AGENT] Web research configuration error: {ex.Message}");
            return new LearningReport(false, ex.Message, [], [], []);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError($"[AGENT] Web research HTTP error: {ex.Message}");
            return new LearningReport(
                false,
                $"Falha na pesquisa web: {ex.Message}",
                [],
                [],
                []);
        }
        catch (JsonException ex)
        {
            logger.LogError($"[AGENT] Invalid web research response: {ex.Message}");
            return new LearningReport(
                false,
                "O provider de pesquisa web retornou uma resposta inválida.",
                [],
                [],
                []);
        }
        catch (TaskCanceledException ex)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError($"[AGENT] Web research timed out: {ex.Message}");
            return new LearningReport(
                false,
                "A pesquisa web excedeu o tempo limite configurado.",
                [],
                [],
                []);
        }
    }

    private static KnowledgeItem CreateItem(
        string topic,
        KnowledgeItemDraft draft,
        ResearchResult source,
        KnowledgeClassification classification)
    {
        return new KnowledgeItem
        {
            Domain = classification.Domain,
            Kind = classification.Kind,
            Topic = topic,
            Title = draft.Title,
            Content = draft.Content,
            Summary = string.IsNullOrWhiteSpace(draft.Summary)
                ? draft.Content
                : draft.Summary,
            Examples = string.Join(Environment.NewLine, draft.Examples),
            Warnings = string.Join(Environment.NewLine, draft.Warnings),
            NormalizedCommand = draft.NormalizedCommand,
            Language = draft.Language,
            SourceUrl = source.Url,
            SourceScore = Math.Clamp(source.SourceScore, 0, 1),
            ClassificationConfidence = Math.Clamp(
                classification.Confidence,
                0,
                1),
            SafetyScore = classification.RiskLevel switch
            {
                CommandRiskLevel.Low => 1,
                CommandRiskLevel.Medium => 0.65,
                CommandRiskLevel.High => 0.25,
                _ => 0
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static IReadOnlyList<KnowledgeFact> CreateFacts(
        KnowledgeItem item,
        KnowledgeItemDraft draft)
    {
        var facts = draft.Facts
            .Where(fact => !string.IsNullOrWhiteSpace(fact))
            .Select(fact => fact.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(fact => new KnowledgeFact
            {
                KnowledgeItemId = item.Id,
                Fact = fact,
                Confidence = Math.Clamp(draft.Confidence, 0, 1),
                SourceUrl = draft.SourceUrl
            })
            .ToList();

        if (facts.Count == 0 && !string.IsNullOrWhiteSpace(item.Summary))
        {
            facts.Add(new KnowledgeFact
            {
                KnowledgeItemId = item.Id,
                Fact = item.Summary,
                Confidence = Math.Clamp(draft.Confidence, 0, 1),
                SourceUrl = draft.SourceUrl
            });
        }

        return facts;
    }

    private static bool IsMissingConfigurationError(string message) =>
        message.Equals(
            "Web research provider is disabled.",
            StringComparison.Ordinal) ||
        message.Equals(
            "Brave Search API key is not configured.",
            StringComparison.Ordinal);

    private static void ApplyVerificationScore(
        KnowledgeItem item,
        KnowledgeExperiment experiment)
    {
        item.VerificationScore = experiment.VerificationKind switch
        {
            VerificationKind.NotTestableLocally => 0.65,
            VerificationKind.SourceOnly => 0.55,
            _ when experiment.Success => 1,
            _ => 0.10
        };
    }
}
