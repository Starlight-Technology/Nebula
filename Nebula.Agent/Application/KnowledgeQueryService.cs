using System.Globalization;

using Nebula.Core.Learning;

namespace Nebula.Agent.Application;

public sealed class KnowledgeQueryService(
    IKnowledgeStore store,
    ILogger logger,
    IKnowledgeAutomationPolicy? automationPolicy = null) : IKnowledgeQueryService
{
    /// <summary>
    /// Answers a knowledge-base question with stored evidence and diagnostic metadata.
    /// </summary>
    public Task<string> AnswerAsync(
        string topic,
        CancellationToken cancellationToken = default)
    {
        return AnswerInternalAsync(topic, requireAutomatable: false, cancellationToken);
    }

    public Task<string> AnswerForAutomationAsync(
        string topic,
        CancellationToken cancellationToken = default)
    {
        return AnswerInternalAsync(topic, requireAutomatable: true, cancellationToken);
    }

    private async Task<string> AnswerInternalAsync(
        string topic,
        bool requireAutomatable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        var results = await store.FindDetailsAsync(
            topic.Trim(),
            minimumScore: requireAutomatable ? 0.60 : 0.60,
            cancellationToken);
        if (requireAutomatable &&
            automationPolicy is not null)
        {
            results = results
                .Where(result => automationPolicy.CanUseAutomatically(result.Item))
                .ToList();
        }

        if (results.Count == 0)
        {
            return $"Não há conhecimento armazenado sobre '{topic.Trim()}'.";
        }

        logger.Log(
            $"[AGENT] Knowledge query: topic={topic.Trim()}; " +
            $"resultCount={results.Count}");
        var lines = new List<string>();
        foreach (var result in results.Take(5))
        {
            var item = result.Item;
            var experiment = result.Experiments
                .OrderByDescending(value => value.CreatedAt)
                .FirstOrDefault();
            lines.Add(item.Title);
            lines.Add(string.IsNullOrWhiteSpace(item.Summary)
                ? item.Content
                : item.Summary);
            lines.Add($"Fonte: {item.SourceUrl}");
            lines.Add(
                $"Score: {item.FinalScore.ToString("F2", CultureInfo.InvariantCulture)}");
            lines.Add($"Aprendido em: {item.UpdatedAt:O}");
            lines.Add(experiment is null
                ? "Validação local: não registrada."
                : $"Validação local: {experiment.VerificationKind}; sucesso={experiment.Success}.");
            if (result.Facts.Count > 0)
            {
                lines.Add("Fatos:");
                lines.AddRange(result.Facts.Take(5).Select(fact =>
                    $"- {fact.Fact} (confiança " +
                    $"{fact.Confidence.ToString("F2", CultureInfo.InvariantCulture)})"));
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }
}
