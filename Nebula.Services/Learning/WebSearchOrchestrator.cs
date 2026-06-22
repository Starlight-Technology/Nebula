using Nebula.Core.Learning;

namespace Nebula.Services.Learning;

public sealed class WebSearchOrchestrator(
    IEnumerable<ISearchProvider> providers,
    WebResearchLogSink? logSink = null) : IWebSearchOrchestrator
{
    private readonly IReadOnlyList<ISearchProvider> providers = providers.ToList();

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var limit = Math.Clamp(maxResults, 1, 50);
        var results = new List<SearchResult>();
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var providerResults = await provider.SearchAsync(
                    query,
                    cancellationToken);
                results.AddRange(providerResults);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logSink?.Write(
                    $"[AGENT] Web research: provider={provider.GetType().Name}; " +
                    $"failed=true; error={ex.Message}");
            }
        }

        return results
            .Where(result => Uri.TryCreate(result.Url, UriKind.Absolute, out _))
            .GroupBy(result => NormalizeUrl(result.Url), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(result => result.SearchScore).First())
            .OrderByDescending(result => result.SearchScore)
            .Take(limit)
            .ToList();
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Trim();
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
