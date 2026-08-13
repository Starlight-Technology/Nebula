namespace Nebula.Core.Learning;

public interface ISearchProvider
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken);
}

public interface IWebSearchOrchestrator
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken);
}

public interface IPageFetcher
{
    Task<PageContent> FetchAsync(
        string url,
        CancellationToken cancellationToken);
}

public interface IContentExtractor
{
    ExtractedContent Extract(PageContent page);
}

public interface IFetchedPageCache
{
    Task<FetchedPageCacheEntry?> GetAsync(
        string url,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        FetchedPageCacheEntry entry,
        CancellationToken cancellationToken = default);
}

public interface IDomainRateLimiter
{
    Task WaitAsync(
        Uri uri,
        CancellationToken cancellationToken = default);
}

public interface IWebResearchService
{
    Task<IReadOnlyList<ResearchResult>> SearchAsync(
        string topic,
        KnowledgeDomain domain,
        CancellationToken cancellationToken);
}

public interface IResearchProvider
{
    string Name { get; }

    LearningSourceType SourceType { get; }

    bool IsConfigured { get; }

    /// <summary>
    /// Searches this provider for documents that can support the learning objective.
    /// </summary>
    Task<IReadOnlyList<LearningSourceDocument>> SearchAsync(
        string objective,
        KnowledgeDomain domain,
        CancellationToken cancellationToken = default);
}

public interface IKnowledgeExtractor
{
    Task<IReadOnlyList<KnowledgeItemDraft>> ExtractAsync(
        string topic,
        KnowledgeDomain domain,
        IReadOnlyList<ResearchResult> sources,
        CancellationToken cancellationToken);
}

public interface IKnowledgeClassifier
{
    Task<KnowledgeClassification> ClassifyAsync(
        KnowledgeItemDraft draft,
        CancellationToken cancellationToken = default);
}

public interface IKnowledgeRiskClassifier
{
    /// <summary>
    /// Classifies the operational risk of an extracted knowledge draft.
    /// </summary>
    KnowledgeRiskAssessment Classify(KnowledgeItemDraft draft);
}

public interface ILearningSourceReader
{
    /// <summary>
    /// Reads local files and converts their contents into learning source documents.
    /// </summary>
    Task<IReadOnlyList<LearningSourceDocument>> ReadFilesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads explicit web pages and converts their contents into learning source documents.
    /// </summary>
    Task<IReadOnlyList<LearningSourceDocument>> ReadSitesAsync(
        IReadOnlyList<string> siteUrls,
        CancellationToken cancellationToken = default);
}

public interface ISafeExperimentRunner
{
    Task<KnowledgeExperiment> TryVerifyAsync(
        KnowledgeItem item,
        CancellationToken cancellationToken);
}

public interface IKnowledgeScoreEngine
{
    double Calculate(KnowledgeItem item);
}

public interface IKnowledgeStore
{
    Task SaveAsync(
        KnowledgeItem item,
        IReadOnlyList<KnowledgeSource> sources,
        IReadOnlyList<KnowledgeFact> facts,
        KnowledgeExperiment experiment,
        CancellationToken cancellationToken = default);

    Task UpdateExperimentAsync(
        Guid experimentId,
        KnowledgeExperiment updated,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeItem>> FindTrustedAsync(
        KnowledgeDomain domain,
        string topic,
        double minimumScore = 0.75,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnowledgeLookupResult>> FindDetailsAsync(
        string topic,
        double minimumScore = 0.75,
        CancellationToken cancellationToken = default);
}

public interface IKnowledgeRepository : IKnowledgeStore
{
    /// <summary>
    /// Finds a knowledge item and its evidence by deterministic hash.
    /// </summary>
    Task<KnowledgeLookupResult?> FindByHashAsync(
        string hash,
        CancellationToken cancellationToken = default);
}

public interface ILearningOrchestrator
{
    /// <summary>
    /// Coordinates provider lookup, extraction, risk classification, deduplication, and persistence.
    /// </summary>
    Task<LearningResult> LearnAsync(
        LearningOptions options,
        CancellationToken cancellationToken = default);
}

public interface ILearningEngine
{
    Task<LearningReport> LearnAsync(
        LearningRequest request,
        CancellationToken cancellationToken = default);
}

public interface IKnowledgeAutomationPolicy
{
    bool CanUseAutomatically(KnowledgeItem item);
}

public interface IKnowledgeQueryService
{
    Task<string> AnswerAsync(
        string topic,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns knowledge that the automation policy trusts for automatic reuse
    /// (high score, non-dangerous). Falls back to <see cref="AnswerAsync"/>.
    /// </summary>
    Task<string> AnswerForAutomationAsync(
        string topic,
        CancellationToken cancellationToken = default)
        => AnswerAsync(topic, cancellationToken);
}
