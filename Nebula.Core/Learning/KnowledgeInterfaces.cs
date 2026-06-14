namespace Nebula.Core.Learning;

public interface ISearchProvider
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
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
}
