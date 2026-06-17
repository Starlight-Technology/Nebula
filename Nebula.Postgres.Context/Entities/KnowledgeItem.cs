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

    public string Tags { get; set; } = string.Empty;

    public string? NormalizedCommand { get; set; }

    public string? Language { get; set; }

    public string? OS { get; set; }

    public string? Shell { get; set; }

    public string SourceUrl { get; set; } = string.Empty;

    public LearningSourceType SourceType { get; set; } =
        LearningSourceType.WebResearch;

    public string SourceName { get; set; } = string.Empty;

    public KnowledgeRiskLevel RiskLevel { get; set; } =
        KnowledgeRiskLevel.Unknown;

    public double ConfidenceScore { get; set; }

    public double SourceScore { get; set; }

    public double ClassificationConfidence { get; set; }

    public double SafetyScore { get; set; }

    public double VerificationScore { get; set; }

    public double FinalScore { get; set; }

    public string Hash { get; set; } = string.Empty;

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public int ObservationCount { get; set; } = 1;

    public bool IsExecutableAdvice { get; set; }

    public bool IsDangerousInstruction { get; set; }

    public bool IsValidated { get; set; }

    public string ValidationNotes { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<KnowledgeExperiment> Experiments { get; set; } = [];

    public ICollection<KnowledgeSource> Sources { get; set; } = [];

    public ICollection<KnowledgeFact> Facts { get; set; } = [];
}
