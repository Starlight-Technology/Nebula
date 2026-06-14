using Nebula.Core.Safety;

namespace Nebula.Core.Learning;

public enum KnowledgeDomain
{
    WindowsCommands,
    PowerShell,
    LinuxCommands,
    Python,
    DotNet,
    Mathematics,
    Physics,
    Chemistry,
    General
}

public enum KnowledgeItemKind
{
    Command,
    CodeSnippet,
    Concept,
    Formula,
    Procedure,
    Warning,
    Example
}

public enum VerificationKind
{
    SourceOnly,
    StaticAnalysis,
    SafeExecution,
    UnitTest,
    NumericCheck,
    SymbolicCheck,
    NotTestableLocally
}

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
}

public sealed class KnowledgeExperiment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid KnowledgeItemId { get; set; }

    public VerificationKind VerificationKind { get; set; }

    public string? CommandExecuted { get; set; }

    public string? TestCode { get; set; }

    public int? ExitCode { get; set; }

    public string? StdOut { get; set; }

    public string? StdErr { get; set; }

    public bool Success { get; set; }

    public string EvidenceHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class KnowledgeSource
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid KnowledgeItemId { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Publisher { get; set; } = string.Empty;

    public string ExtractedContent { get; set; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset RetrievedAt { get; set; } = DateTimeOffset.UtcNow;

    public double TrustScore { get; set; }
}

public sealed class KnowledgeFact
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid KnowledgeItemId { get; set; }

    public string Fact { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public string SourceUrl { get; set; } = string.Empty;
}

public sealed record ResearchResult(
    string Title,
    string Url,
    string Snippet,
    string? Publisher,
    DateTimeOffset RetrievedAt,
    double SourceScore);

public sealed record SearchResult(
    string Title,
    string Url,
    string Snippet,
    double SearchScore);

public sealed record PageContent(
    string Url,
    string Html,
    DateTimeOffset RetrievedAt);

public sealed record ExtractedContent(
    string Url,
    string Title,
    string Content,
    IReadOnlyList<string> CodeBlocks);

public sealed record FetchedPageCacheEntry(
    string Url,
    string Html,
    string HtmlHash,
    DateTimeOffset RetrievedAt,
    DateTimeOffset ExpiresAt);

public sealed class KnowledgeItemDraft
{
    public string SourceUrl { get; set; } = string.Empty;

    public string EvidenceSummary { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public KnowledgeDomain Domain { get; set; }

    public KnowledgeItemKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public List<string> Examples { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<string> Facts { get; set; } = [];

    public string? NormalizedCommand { get; set; }

    public string? Language { get; set; }

    public bool ExecutableLocally { get; set; }
}

public sealed record KnowledgeClassification(
    KnowledgeDomain Domain,
    KnowledgeItemKind Kind,
    CommandRiskLevel RiskLevel,
    double Confidence,
    string Source,
    IReadOnlyList<string> Reasons);

public sealed record LearningRequest(
    string Topic,
    KnowledgeDomain Domain);

public sealed record LearningReport(
    bool Success,
    string? Error,
    IReadOnlyList<KnowledgeItem> Items,
    IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyList<KnowledgeExperiment> Experiments,
    IReadOnlyList<KnowledgeFact>? Facts = null);

public sealed record KnowledgeLookupResult(
    KnowledgeItem Item,
    IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyList<KnowledgeExperiment> Experiments,
    IReadOnlyList<KnowledgeFact> Facts);
