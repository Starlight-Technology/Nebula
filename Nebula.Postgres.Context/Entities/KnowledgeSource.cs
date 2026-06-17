namespace Nebula.Postgres.Context.Entities;

public sealed class KnowledgeSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid KnowledgeItemId { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Publisher { get; set; } = string.Empty;

    public string ProviderName { get; set; } = string.Empty;

    public Nebula.Core.Learning.LearningSourceType SourceType { get; set; } =
        Nebula.Core.Learning.LearningSourceType.WebResearch;

    public string ExtractedContent { get; set; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;

    public double TrustScore { get; set; }

    public KnowledgeItem? KnowledgeItem { get; set; }
}
