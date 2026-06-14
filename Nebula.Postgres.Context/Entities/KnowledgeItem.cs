using Nebula.Core.Learning;

namespace Nebula.Postgres.Context.Entities;

public sealed class KnowledgeItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public KnowledgeDomain Domain { get; set; }

    public KnowledgeItemKind Kind { get; set; }

    public string Topic { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Examples { get; set; } = string.Empty;

    public string Warnings { get; set; } = string.Empty;

    public string? NormalizedCommand { get; set; }

    public string? Language { get; set; }

    public string? OS { get; set; }

    public string? Shell { get; set; }

    public string SourceUrl { get; set; } = string.Empty;

    public double SourceScore { get; set; }

    public double ClassificationConfidence { get; set; }

    public double SafetyScore { get; set; }

    public double VerificationScore { get; set; }

    public double FinalScore { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<KnowledgeExperiment> Experiments { get; set; } = [];

    public ICollection<KnowledgeSource> Sources { get; set; } = [];

    public ICollection<KnowledgeFact> Facts { get; set; } = [];
}
