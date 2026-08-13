using Nebula.Core.Learning;
using Nebula.Services.Learning;

namespace Nebula.Agent.Application;

public sealed class LearningEngine : ILearningEngine
{
    private readonly ILearningOrchestrator orchestrator;
    private readonly ILogger logger;

    public LearningEngine(
        ILearningOrchestrator orchestrator,
        ILogger logger)
    {
        this.orchestrator = orchestrator;
        this.logger = logger;
    }

    public LearningEngine(
        IWebResearchService researchService,
        IKnowledgeExtractor extractor,
        IKnowledgeClassifier classifier,
        IKnowledgeStore store,
        ISafeExperimentRunner experimentRunner,
        IKnowledgeScoreEngine scoreEngine,
        ILogger logger,
        ILearningSourceReader? sourceReader = null)
        : this(
            new LearningOrchestrator(
                CreateDefaultProviders(researchService),
                extractor,
                classifier,
                new KnowledgeRiskClassifier(),
                store,
                scoreEngine,
                experimentRunner,
                sourceReader,
                log: logger.Log),
            logger)
    {
    }

    /// <summary>
    /// Learns structured knowledge and returns a report suitable for agent responses.
    /// </summary>
    public async Task<LearningReport> LearnAsync(
        LearningRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await orchestrator.LearnAsync(
            LearningOptions.FromRequest(request),
            cancellationToken);
        foreach (var diagnostic in result.ProviderDiagnostics)
        {
            logger.Log(
                $"[AGENT] {diagnostic.ProviderName}: " +
                $"{(diagnostic.IsConfigured ? "enabled" : "disabled")}; " +
                $"{diagnostic.DocumentsFound} documents" +
                (string.IsNullOrWhiteSpace(diagnostic.Message)
                    ? string.Empty
                    : $"; {diagnostic.Message}"));
        }

        if (!result.Success)
        {
            logger.LogError($"[AGENT] Learning failed: {result.Message}");
        }

        return new LearningReport(
            result.Success,
            result.Success ? null : BuildFailureMessage(request, result),
            result.KnowledgeItems,
            result.Sources,
            result.Experiments,
            result.Facts,
            result.CreatedCount,
            result.UpdatedCount,
            result.SkippedCount,
            result.DangerousCount,
            result.DocumentsFound,
            result.Warnings,
            result.Errors,
            result.ProviderDiagnostics);
    }

    private static string BuildFailureMessage(
        LearningRequest request,
        LearningResult result)
    {
        var lines = new List<string>
        {
            $"Objetivo: {request.Topic}",
            result.Message
        };
        if (result.Warnings.Count > 0)
        {
            lines.Add("Warnings:");
            lines.AddRange(result.Warnings.Select(warning => $"- {warning}"));
        }

        if (result.ProviderDiagnostics.Count > 0)
        {
            lines.Add("Providers consultados:");
            lines.AddRange(result.ProviderDiagnostics.Select(diagnostic =>
                $"- {diagnostic.ProviderName}: " +
                $"{(diagnostic.IsConfigured ? "enabled" : "disabled")}; " +
                $"{diagnostic.DocumentsFound} documents" +
                (string.IsNullOrWhiteSpace(diagnostic.Message)
                    ? string.Empty
                    : $"; {diagnostic.Message}")));
        }

        if (result.Errors.Count > 0)
        {
            lines.Add("Erros:");
            lines.AddRange(result.Errors.Select(error => $"- {error}"));
        }

        lines.Add(
            "Sugestao: habilite ManualSeedResearchProvider, forneca texto local ou configure um WebResearchProvider.");
        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<IResearchProvider> CreateDefaultProviders(
        IWebResearchService researchService)
    {
        var webConfigured = researchService is not DisabledWebResearchService;
        return webConfigured
            ?
            [
                new WebResearchProvider(researchService, webConfigured)
            ]
            :
            [
                new ManualSeedResearchProvider(),
                new WebResearchProvider(researchService, webConfigured)
            ];
    }
}
