namespace Nebula.Postgres.Context.Entities;

public sealed class KnowledgeFact
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid KnowledgeItemId { get; set; }

    public string Fact { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public string SourceUrl { get; set; } = string.Empty;

    public KnowledgeItem? KnowledgeItem { get; set; }
}
