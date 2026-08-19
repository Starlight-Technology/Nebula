using System.Globalization;

using Nebula.Core.Learning;

namespace Nebula.Agent.Application;

public sealed class KnowledgeQueryService(
    IKnowledgeStore store,
    ILogger logger,
    IKnowledgeAutomationPolicy? automationPolicy = null,
    TimeSpan? stalenessThreshold = null) : IKnowledgeQueryService
{
    private readonly TimeSpan stalenessThreshold =
        stalenessThreshold ?? TimeSpan.FromDays(90);

    /// <summary>
    /// Answers a knowledge-base question with stored evidence and diagnostic metadata.
    /// Stale items (not observed for a long time) are still shown but flagged as
    /// possibly outdated so the user can decide to relearn.
    /// </summary>
    public Task<string> AnswerAsync(
        string topic,
        CancellationToken cancellationToken = default)
    {
        return AnswerInternalAsync(topic, requireAutomatable: false, cancellationToken);
    }

    /// <summary>
    /// Returns knowledge that the automation policy trusts for automatic reuse
    /// (high score, non-dangerous, not stale). Stale and non-trusted items are
    /// never injected automatically, which forces a human review before reuse.
    /// </summary>
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
            minimumScore: 0.60,
            cancellationToken);
        if (requireAutomatable &&
            automationPolicy is not null)
        {
            results = results
                .Where(result => automationPolicy.CanUseAutomatically(result.Item))
                .ToList();
        }

        var staleExcluded = false;
        if (requireAutomatable)
        {
            var fresh = results
                .Where(result => !IsStale(result.Item))
                .ToList();
            staleExcluded = fresh.Count < results.Count;
            results = fresh;
        }

        if (results.Count == 0)
        {
            var mustReview = requireAutomatable && staleExcluded
                ? " Itens existentes estao desatualizados " +
                  $"(ultima confirmacao ha mais de {stalenessThreshold.TotalDays:F0} dias) " +
                  "e nao sao usados automaticamente; reavalie antes de reutilizar."
                : string.Empty;
            return $"Não há conhecimento armazenado sobre '{topic.Trim()}'.{mustReview}";
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
            if (IsStale(item))
            {
                lines.Add(
                    $"Alerta: conhecimento desatualizado " +
                    $"(ultima confirmacao ha {AgeDays(item):F0} dias); " +
                    "pode precisar de reavaliacao/reaprendizado.");
            }

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

    private bool IsStale(KnowledgeItem item) =>
        DateTimeOffset.UtcNow - item.LastSeenAt > stalenessThreshold;

    private static double AgeDays(KnowledgeItem item) =>
        (DateTimeOffset.UtcNow - item.LastSeenAt).TotalDays;
}