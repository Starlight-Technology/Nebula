using Nebula.Core.Safety;

namespace Nebula.Core.Learning;

public enum KnowledgeDomain
{
    WindowsCommands,
    PowerShell,
    LinuxCommands,
    ShellSecurity,
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

public enum KnowledgeRiskLevel
{
    Safe,
    LowRisk,
    MediumRisk,
    HighRisk,
    Dangerous,
    Unknown
}

public enum LearningSourceType
{
    UserProvidedText,
    ManualSeed,
    FakeResearch,
    WebResearch,
    LocalFile,
    ExistingKnowledgeBase
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

    public string ProviderName { get; set; } = string.Empty;

    public LearningSourceType SourceType { get; set; } =
        LearningSourceType.WebResearch;

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

    public List<string> Tags { get; set; } = [];

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

public sealed record KnowledgeRiskAssessment(
    KnowledgeRiskLevel RiskLevel,
    double ConfidenceScore,
    bool IsExecutableAdvice,
    bool IsDangerousInstruction,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Reasons);

public sealed record LearningRequest(
    string Topic,
    KnowledgeDomain Domain,
    string? UserProvidedText = null,
    IReadOnlyList<string>? SourceFilePaths = null,
    IReadOnlyList<string>? SourceUrls = null);

public sealed record LearningReport(
    bool Success,
    string? Error,
    IReadOnlyList<KnowledgeItem> Items,
    IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyList<KnowledgeExperiment> Experiments,
    IReadOnlyList<KnowledgeFact>? Facts = null,
    int CreatedCount = 0,
    int UpdatedCount = 0,
    int SkippedCount = 0,
    int DangerousCount = 0,
    int DocumentsFound = 0,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<LearningProviderDiagnostic>? ProviderDiagnostics = null);

public sealed record KnowledgeLookupResult(
    KnowledgeItem Item,
    IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyList<KnowledgeExperiment> Experiments,
    IReadOnlyList<KnowledgeFact> Facts);

public sealed record KnowledgeEvidence(
    string SourceTitle,
    string? SourceUri,
    string ProviderName,
    DateTimeOffset RetrievedAt,
    string Excerpt,
    double ConfidenceScore);

public sealed record LearningSourceDocument(
    string Title,
    string Content,
    string? SourceUri,
    string ProviderName,
    DateTimeOffset RetrievedAt,
    LearningSourceType SourceType);

public sealed class LearningOptions
{
    public string Objective { get; init; } = string.Empty;

    public KnowledgeDomain Domain { get; init; } = KnowledgeDomain.General;

    public string? UserProvidedText { get; init; }

    public IReadOnlyList<string> SourceFilePaths { get; init; } = [];

    public IReadOnlyList<string> SourceUrls { get; init; } = [];

    public bool IncludeManualSeeds { get; init; } = true;

    public bool IncludeWebResearch { get; init; } = true;

    public IReadOnlyList<LearningSourceDocument> AdditionalDocuments { get; init; } = [];

    /// <summary>
    /// Creates orchestrator options from the public learning request contract.
    /// </summary>
    public static LearningOptions FromRequest(LearningRequest request) =>
        new()
        {
            Objective = request.Topic,
            Domain = request.Domain,
            UserProvidedText = request.UserProvidedText,
            SourceFilePaths = request.SourceFilePaths ?? [],
            SourceUrls = request.SourceUrls ?? []
        };
}

public sealed record LearningProviderDiagnostic(
    string ProviderName,
    LearningSourceType SourceType,
    bool IsConfigured,
    int DocumentsFound,
    string? Message);

public sealed record LearningResult(
    bool Success,
    string Message,
    int CreatedCount,
    int UpdatedCount,
    int SkippedCount,
    int DangerousCount,
    int DocumentsFound,
    IReadOnlyList<KnowledgeItem> KnowledgeItems,
    IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyList<KnowledgeExperiment> Experiments,
    IReadOnlyList<KnowledgeFact> Facts,
    IReadOnlyList<KnowledgeEvidence> Evidence,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<LearningProviderDiagnostic> ProviderDiagnostics);
