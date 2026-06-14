using Microsoft.ML;

using Nebula.Core.Learning;
using Nebula.Core.Safety;

namespace Nebula.Services.Learning;

public sealed class KnowledgeScoreEngine : IKnowledgeScoreEngine
{
    public double Calculate(KnowledgeItem item) =>
        Math.Clamp(
            item.SourceScore * 0.35 +
            item.ClassificationConfidence * 0.20 +
            item.SafetyScore * 0.20 +
            item.VerificationScore * 0.25,
            0,
            1);
}

public sealed class KnowledgeAutomationPolicy : IKnowledgeAutomationPolicy
{
    public bool CanUseAutomatically(KnowledgeItem item) =>
        item.FinalScore >= 0.75;
}

public sealed class InMemoryKnowledgeStore : IKnowledgeStore
{
    private readonly List<KnowledgeLookupResult> entries = [];

    public Task SaveAsync(
        KnowledgeItem item,
        IReadOnlyList<KnowledgeSource> sources,
        IReadOnlyList<KnowledgeFact> facts,
        KnowledgeExperiment experiment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        entries.RemoveAll(existing => existing.Item.Id == item.Id);
        entries.Add(new KnowledgeLookupResult(
            item,
            sources,
            [experiment],
            facts));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<KnowledgeItem>> FindTrustedAsync(
        KnowledgeDomain domain,
        string topic,
        double minimumScore = 0.75,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<KnowledgeItem> result = entries
            .Select(entry => entry.Item)
            .Where(item =>
                item.Domain == domain &&
                Matches(item, topic) &&
                item.FinalScore >= minimumScore)
            .OrderByDescending(item => item.FinalScore)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<KnowledgeLookupResult>> FindDetailsAsync(
        string topic,
        double minimumScore = 0.75,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<KnowledgeLookupResult> result = entries
            .Where(entry =>
                entry.Item.FinalScore >= minimumScore &&
                Matches(entry.Item, topic))
            .OrderByDescending(entry => entry.Item.FinalScore)
            .ToList();
        return Task.FromResult(result);
    }

    private static bool Matches(KnowledgeItem item, string topic) =>
        item.Topic.Contains(topic, StringComparison.OrdinalIgnoreCase) ||
        item.Title.Contains(topic, StringComparison.OrdinalIgnoreCase) ||
        item.Content.Contains(topic, StringComparison.OrdinalIgnoreCase) ||
        item.Summary.Contains(topic, StringComparison.OrdinalIgnoreCase);
}

public sealed class KnowledgeClassificationPipeline : IKnowledgeClassifier
{
    private readonly object predictionLock = new();
    private readonly PredictionEngine<KnowledgeModelInput, KnowledgeModelPrediction>? engine;
    private readonly Action<string>? log;

    public KnowledgeClassificationPipeline(
        string? modelPath = null,
        Action<string>? log = null)
    {
        this.log = log;
        ModelPath = Path.GetFullPath(
            modelPath ??
            Path.Combine(
                AppContext.BaseDirectory,
                "models",
                "knowledge-classifier.zip"));

        if (!File.Exists(ModelPath))
        {
            log?.Invoke(
                $"Warning: ML.NET knowledge model was not found at '{ModelPath}'. " +
                "Knowledge classification will continue with deterministic heuristics.");
            return;
        }

        var context = new MLContext();
        var model = context.Model.Load(ModelPath, out _);
        engine = context.Model.CreatePredictionEngine<
            KnowledgeModelInput,
            KnowledgeModelPrediction>(model);
    }

    public string ModelPath { get; }

    public Task<KnowledgeClassification> ClassifyAsync(
        KnowledgeItemDraft draft,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var heuristic = ClassifyHeuristically(draft);
        if (heuristic.Confidence >= 0.90 || engine is null)
        {
            return Task.FromResult(heuristic);
        }

        KnowledgeModelPrediction prediction;
        lock (predictionLock)
        {
            prediction = engine.Predict(new KnowledgeModelInput
            {
                Text = $"{draft.Title} {draft.Content}"
            });
        }

        var parts = prediction.PredictedLabel.Split('|');
        var domain = parts.Length > 0 &&
                     Enum.TryParse(parts[0], true, out KnowledgeDomain parsedDomain)
            ? parsedDomain
            : heuristic.Domain;
        var kind = parts.Length > 1 &&
                   Enum.TryParse(parts[1], true, out KnowledgeItemKind parsedKind)
            ? parsedKind
            : heuristic.Kind;
        var risk = parts.Length > 2 &&
                   Enum.TryParse(parts[2], true, out CommandRiskLevel parsedRisk)
            ? parsedRisk
            : heuristic.RiskLevel;
        var confidence = prediction.Score.Length == 0
            ? heuristic.Confidence
            : prediction.Score.Max();

        return Task.FromResult(new KnowledgeClassification(
            domain,
            kind,
            risk,
            confidence,
            nameof(KnowledgeClassificationPipeline),
            [$"ML.NET predicted '{prediction.PredictedLabel}'."]));
    }

    private static KnowledgeClassification ClassifyHeuristically(
        KnowledgeItemDraft draft)
    {
        var text = $"{draft.Title} {draft.Content}".ToLowerInvariant();
        var domain = draft.Domain != KnowledgeDomain.General
            ? draft.Domain
            : text switch
            {
                var value when value.Contains("powershell") =>
                    KnowledgeDomain.PowerShell,
                var value when value.Contains("python") =>
                    KnowledgeDomain.Python,
                var value when value.Contains("dotnet") ||
                                   value.Contains(".net") =>
                    KnowledgeDomain.DotNet,
                var value when value.Contains("linux") ||
                                   value.Contains("bash") =>
                    KnowledgeDomain.LinuxCommands,
                _ => KnowledgeDomain.General
            };
        var risk = text.Contains("delete") ||
                   text.Contains("remove-item") ||
                   text.Contains("rm -rf") ||
                   text.Contains("network") ||
                   text.Contains("http")
            ? CommandRiskLevel.High
            : CommandRiskLevel.Low;

        return new KnowledgeClassification(
            domain,
            draft.Kind,
            risk,
            draft.Confidence > 0
                ? Math.Clamp(draft.Confidence, 0, 1)
                : 0.90,
            "DeterministicKnowledgeHeuristics",
            ["Knowledge domain, kind, and risk were classified with deterministic heuristics."]);
    }

    private sealed class KnowledgeModelInput
    {
        public string Text { get; set; } = string.Empty;
    }

    private sealed class KnowledgeModelPrediction
    {
        public string PredictedLabel { get; set; } = string.Empty;

        public float[] Score { get; set; } = [];
    }
}
