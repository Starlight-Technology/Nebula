using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using HtmlAgilityPack;

using Nebula.Core.Configuration;
using Nebula.Core.Learning;

namespace Nebula.Services.Learning;

public sealed class ConfigurableSearchProvider(
    NebulaRuntimeSettings runtimeSettings,
    WebResearchOptions configuredOptions,
    DirectDocumentationProvider directProvider,
    BingHtmlSearchProvider bingProvider,
    SearXngSearchProvider searXngProvider,
    FreeSearchProvider freeProvider) : ISearchProvider
{
    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var provider = string.IsNullOrWhiteSpace(runtimeSettings.WebResearchProvider)
            ? configuredOptions.Provider
            : runtimeSettings.WebResearchProvider;

        return provider.Trim().ToLowerInvariant() switch
        {
            "directdocumentation" =>
                directProvider.SearchAsync(query, cancellationToken),
            "searxng" =>
                searXngProvider.SearchAsync(query, cancellationToken),
            "bing" or "binghtml" =>
                bingProvider.SearchAsync(query, cancellationToken),
            _ => freeProvider.SearchAsync(query, cancellationToken)
        };
    }
}

public sealed class DirectDocumentationProvider : ISearchProvider
{
    private static readonly DocumentationTarget[] Targets =
    [
        new(
            ["get-childitem"],
            "Get-ChildItem documentation",
            "https://learn.microsoft.com/powershell/module/microsoft.powershell.management/get-childitem",
            "Official Microsoft documentation for Get-ChildItem."),
        new(
            ["powershell", "get-childitem", "copy-item"],
            "PowerShell documentation",
            "https://learn.microsoft.com/powershell/",
            "Official Microsoft PowerShell documentation."),
        new(
            ["entity framework", "entityframework", "ef core"],
            "Entity Framework Core documentation",
            "https://learn.microsoft.com/ef/core/",
            "Official Microsoft Entity Framework Core documentation."),
        new(
            [".net", "dotnet", "dotnet cli"],
            ".NET CLI documentation",
            "https://learn.microsoft.com/dotnet/core/tools/",
            "Official Microsoft .NET CLI documentation."),
        new(
            ["windows command", "cmd", " dir ", "mkdir", "copy command"],
            "Windows command documentation",
            "https://learn.microsoft.com/windows-server/administration/windows-commands/windows-commands",
            "Official Microsoft Windows command documentation."),
        new(
            ["python", "json.dumps", " print "],
            "Python documentation",
            "https://docs.python.org/3/",
            "Official Python documentation."),
        new(
            ["linux", "bash", "linux command"],
            "Linux manual pages",
            "https://man7.org/linux/man-pages/",
            "Linux manual pages maintained by the Linux man-pages project."),
        new(
            ["mathematics", "math formula", "matemática", "matematica"],
            "Mathematics reference",
            "https://en.wikipedia.org/wiki/Mathematics",
            "Public mathematics reference with cited sources."),
        new(
            ["physics", "física", "fisica"],
            "Physics reference",
            "https://en.wikipedia.org/wiki/Physics",
            "Public physics reference with cited sources."),
        new(
            ["chemistry", "química", "quimica"],
            "Chemistry reference",
            "https://en.wikipedia.org/wiki/Chemistry",
            "Public chemistry reference with cited sources."),
        new(
            ["kubernetes", "kubectl"],
            "Kubernetes documentation",
            "https://kubernetes.io/docs/",
            "Official Kubernetes documentation."),
        new(
            ["docker", "dockerfile"],
            "Docker documentation",
            "https://docs.docker.com/",
            "Official Docker documentation."),
        new(
            ["unity", "unity3d"],
            "Unity documentation",
            "https://docs.unity3d.com/",
            "Official Unity documentation.")
    ];

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var searchable = $" {query.Trim().ToLowerInvariant()} ";
        IReadOnlyList<SearchResult> results = Targets
            .Where(target => target.Terms.Any(searchable.Contains))
            .Select(target => new SearchResult(
                target.Title,
                target.Url,
                target.Snippet,
                1.0))
            .ToList();
        return Task.FromResult(results);
    }

    private sealed record DocumentationTarget(
        IReadOnlyList<string> Terms,
        string Title,
        string Url,
        string Snippet);
}

public sealed class BingHtmlSearchProvider(
    HttpClient httpClient,
    WebResearchOptions options,
    IDomainRateLimiter rateLimiter,
    WebResearchLogSink? logSink = null) : ISearchProvider
{
    private const string SearchEndpoint = "https://www.bing.com/search";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var requestUri = new Uri(
            $"{SearchEndpoint}?q={Uri.EscapeDataString(query)}&count={Math.Clamp(options.MaxResults, 1, 20)}");
        await rateLimiter.WaitAsync(requestUri, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        AddBrowserHeaders(request);
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(
            TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120)));

        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                Log(
                    $"provider=BingHtml; httpStatus={(int)response.StatusCode}; " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds}");
                response.EnsureSuccessStatusCode();
            }

            var html = await response.Content.ReadAsStringAsync(timeout.Token);
            if (html.Contains(
                    "class=\"captcha\"",
                    StringComparison.OrdinalIgnoreCase))
            {
                Log(
                    $"provider=BingHtml; query={query}; captcha=true; " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds}");
                return [];
            }

            var results = Parse(html, Math.Clamp(options.MaxResults, 1, 20));
            Log(
                $"provider=BingHtml; query={query}; resultCount={results.Count}; " +
                $"urls={string.Join(", ", results.Select(result => result.Url))}; " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return results;
        }
        catch (Exception ex) when (
            ex is HttpRequestException ||
            (ex is TaskCanceledException &&
             !cancellationToken.IsCancellationRequested))
        {
            Log(
                $"provider=BingHtml; query={query}; error={ex.Message}; " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            throw;
        }
    }

    public static IReadOnlyList<SearchResult> Parse(
        string html,
        int maxResults)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var resultNodes = document.DocumentNode.SelectNodes(
            "//li[contains(concat(' ', normalize-space(@class), ' '), ' b_algo ')]");
        if (resultNodes is null)
        {
            return [];
        }

        var results = new List<SearchResult>();
        foreach (var node in resultNodes)
        {
            var link = node.SelectSingleNode(".//h2/a[@href]");
            var url = link?.GetAttributeValue("href", string.Empty);
            var title = Clean(link?.InnerText);
            if (string.IsNullOrWhiteSpace(url) ||
                string.IsNullOrWhiteSpace(title) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp &&
                uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            var snippet = Clean(
                node.SelectSingleNode(
                    ".//*[contains(concat(' ', normalize-space(@class), ' '), ' b_caption ')]//p")
                    ?.InnerText);
            results.Add(new SearchResult(
                title,
                url,
                snippet,
                Math.Max(0.5, 0.9 - results.Count * 0.05)));
            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    private static string Clean(string? value) =>
        Regex.Replace(
            HtmlEntity.DeEntitize(value ?? string.Empty),
            @"\s+",
            " ").Trim();

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; NebulaLearningBot/1.0; +https://localhost/nebula)");
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
    }

    private void Log(string message) =>
        logSink?.Write($"[AGENT] Web research: {message}");
}

public sealed class FreeSearchProvider(
    DirectDocumentationProvider directProvider,
    IWebSearchOrchestrator webSearchOrchestrator,
    WebResearchOptions options,
    WebResearchLogSink? logSink = null) : ISearchProvider
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var direct = await directProvider.SearchAsync(query, cancellationToken);
        if (direct.Count > 0)
        {
            logSink?.Write(
                $"[AGENT] Web research: provider=DirectDocumentation; " +
                $"resultCount={direct.Count}; Bing fallback skipped.");
            return direct
                .GroupBy(
                    result => result.Url,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(Math.Clamp(options.MaxResults, 1, 20))
                .ToList();
        }

        var web = await webSearchOrchestrator.SearchAsync(
            query,
            Math.Clamp(options.MaxResults, 1, 20),
            cancellationToken);

        return direct
            .Concat(web)
            .Where(result => Uri.TryCreate(
                result.Url,
                UriKind.Absolute,
                out _))
            .GroupBy(result => result.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.SearchScore).First())
            .OrderByDescending(result => result.SearchScore)
            .Take(Math.Clamp(options.MaxResults, 1, 20))
            .ToList();
    }
}

public sealed class DomainRateLimiter(
    WebResearchOptions options) : IDomainRateLimiter
{
    private readonly ConcurrentDictionary<string, DomainState> domains =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task WaitAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        var state = domains.GetOrAdd(uri.Host, _ => new DomainState());
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var interval = TimeSpan.FromMilliseconds(
                Math.Clamp(options.RateLimitMilliseconds, 100, 10000));
            var delay = state.LastRequestAt + interval - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            state.LastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private sealed class DomainState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset LastRequestAt { get; set; } =
            DateTimeOffset.MinValue;
    }
}

public sealed class InMemoryFetchedPageCache : IFetchedPageCache
{
    private readonly ConcurrentDictionary<string, FetchedPageCacheEntry> entries =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<FetchedPageCacheEntry?> GetAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entries.TryGetValue(url, out var entry) &&
            entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return Task.FromResult<FetchedPageCacheEntry?>(entry);
        }

        entries.TryRemove(url, out _);
        return Task.FromResult<FetchedPageCacheEntry?>(null);
    }

    public Task SetAsync(
        FetchedPageCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        entries[entry.Url] = entry;
        return Task.CompletedTask;
    }
}

public sealed class CachedPageFetcher(
    HttpClient httpClient,
    IFetchedPageCache cache,
    IDomainRateLimiter rateLimiter,
    WebResearchOptions options,
    WebResearchLogSink? logSink = null) : IPageFetcher
{
    public async Task<PageContent> FetchAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var uri = ValidatePublicHttpUrl(url);
        var cached = await cache.GetAsync(url, cancellationToken);
        if (cached is not null)
        {
            Log($"cache=hit; url={url}");
            return new PageContent(
                cached.Url,
                cached.Html,
                cached.RetrievedAt);
        }

        Log($"cache=miss; url={url}");
        var stopwatch = Stopwatch.StartNew();
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await rateLimiter.WaitAsync(uri, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(
                TimeSpan.FromSeconds(
                    Math.Clamp(options.TimeoutSeconds, 1, 120)));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(
                "NebulaLearningBot/1.0 (+https://localhost/nebula)");
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("text/html"));

            try
            {
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                if (IsTransient(response.StatusCode) && attempt < 3)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(250 * attempt),
                        cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 2_000_000)
                {
                    throw new InvalidOperationException(
                        $"URL '{url}' exceeded the 2 MB HTML limit.");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null &&
                    !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"URL '{url}' did not return HTML content.");
                }

                var html = await response.Content.ReadAsStringAsync(timeout.Token);
                if (html.Length > 2_000_000)
                {
                    throw new InvalidOperationException(
                        $"URL '{url}' exceeded the 2 MB HTML limit.");
                }

                var retrievedAt = DateTimeOffset.UtcNow;
                var entry = new FetchedPageCacheEntry(
                    url,
                    html,
                    Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(html))),
                    retrievedAt,
                    retrievedAt.AddDays(Math.Clamp(options.CacheDays, 1, 90)));
                await cache.SetAsync(entry, cancellationToken);
                Log(
                    $"url={url}; elapsedMs={stopwatch.ElapsedMilliseconds}; " +
                    $"bytes={Encoding.UTF8.GetByteCount(html)}");
                return new PageContent(url, html, retrievedAt);
            }
            catch (Exception ex) when (
                ex is HttpRequestException ||
                (ex is TaskCanceledException &&
                 !cancellationToken.IsCancellationRequested))
            {
                lastError = ex;
                if (attempt < 3)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(250 * attempt),
                        cancellationToken);
                }
            }
        }

        Log(
            $"url={url}; error={lastError?.Message}; " +
            $"elapsedMs={stopwatch.ElapsedMilliseconds}");
        throw lastError ?? new HttpRequestException(
            $"Could not fetch '{url}'.");
    }

    private static Uri ValidatePublicHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp &&
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"Only absolute HTTP/HTTPS URLs can be fetched: '{url}'.");
        }

        if (uri.IsLoopback ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(uri.Host, out var address) &&
             IsPrivateAddress(address)))
        {
            throw new InvalidOperationException(
                $"Local or private network URLs cannot be fetched: '{url}'.");
        }

        return uri;
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode == 429 ||
        (int)statusCode >= 500;

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork =>
                bytes[0] == 10 ||
                bytes[0] == 127 ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168),
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                address.IsIPv6LinkLocal ||
                address.IsIPv6SiteLocal ||
                address.Equals(IPAddress.IPv6Loopback),
            _ => true
        };
    }

    private void Log(string message) =>
        logSink?.Write($"[AGENT] Page fetch: {message}");
}

public sealed class HtmlContentExtractor(
    WebResearchLogSink? logSink = null) : IContentExtractor
{
    private const int MaximumContentLength = 40000;

    public ExtractedContent Extract(PageContent page)
    {
        var stopwatch = Stopwatch.StartNew();
        var document = new HtmlDocument();
        document.LoadHtml(page.Html);

        var removable = document.DocumentNode.SelectNodes(
            "//script|//style|//nav|//footer|//header|//aside|//form|//noscript|//svg|//iframe");
        if (removable is not null)
        {
            foreach (var node in removable.ToList())
            {
                node.Remove();
            }
        }

        var title = Normalize(
            document.DocumentNode.SelectSingleNode("//title")?.InnerText);
        var root =
            document.DocumentNode.SelectSingleNode("//main") ??
            document.DocumentNode.SelectSingleNode("//article") ??
            document.DocumentNode.SelectSingleNode("//*[@role='main']") ??
            document.DocumentNode.SelectSingleNode("//body") ??
            document.DocumentNode;
        var contentNodes = root.SelectNodes(
            ".//h1|.//h2|.//h3|.//h4|.//p|.//li|.//pre");
        var parts = contentNodes?
            .Select(node => Normalize(node.InnerText))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        var content = string.Join(Environment.NewLine, parts);
        if (content.Length > MaximumContentLength)
        {
            content = content[..MaximumContentLength];
        }

        var codeBlocks = root
            .SelectNodes(".//pre|.//code")?
            .Select(node => Normalize(node.InnerText))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(30)
            .ToList() ?? [];

        logSink?.Write(
            $"[AGENT] Content extraction: url={page.Url}; " +
            $"elapsedMs={stopwatch.ElapsedMilliseconds}; " +
            $"characters={content.Length}; codeBlocks={codeBlocks.Count}");
        return new ExtractedContent(
            page.Url,
            title,
            content,
            codeBlocks);
    }

    private static string Normalize(string? value) =>
        Regex.Replace(
            HtmlEntity.DeEntitize(value ?? string.Empty),
            @"[ \t\r\f\v]+",
            " ")
        .Replace("\n ", "\n", StringComparison.Ordinal)
        .Trim();
}

public sealed class FreeWebResearchService(
    ISearchProvider searchProvider,
    IPageFetcher pageFetcher,
    IContentExtractor contentExtractor,
    WebResearchOptions options,
    WebResearchLogSink? logSink = null) : IWebResearchService
{
    public async Task<IReadOnlyList<ResearchResult>> SearchAsync(
        string topic,
        KnowledgeDomain domain,
        CancellationToken cancellationToken)
    {
        var query = WebResearchQueryBuilder.Build(topic, domain);
        logSink?.Write(
            $"[AGENT] Web research: provider=Free; query={query}");
        var searchResults = await searchProvider.SearchAsync(
            query,
            cancellationToken);
        var research = new List<ResearchResult>();

        foreach (var result in searchResults.Take(
                     Math.Clamp(options.MaxResults, 1, 20)))
        {
            try
            {
                var page = await pageFetcher.FetchAsync(
                    result.Url,
                    cancellationToken);
                var extracted = contentExtractor.Extract(page);
                if (string.IsNullOrWhiteSpace(extracted.Content))
                {
                    continue;
                }

                var evidence = BuildEvidence(result, extracted);
                research.Add(new ResearchResult(
                    string.IsNullOrWhiteSpace(extracted.Title)
                        ? result.Title
                        : extracted.Title,
                    result.Url,
                    evidence,
                    new Uri(result.Url).Host,
                    page.RetrievedAt,
                    WebResearchSourceScorer.Score(result.Url)));
            }
            catch (Exception ex) when (
                ex is HttpRequestException or InvalidOperationException ||
                (ex is TaskCanceledException &&
                 !cancellationToken.IsCancellationRequested))
            {
                logSink?.Write(
                    $"[AGENT] Web research: url={result.Url}; " +
                    $"skipped=true; error={ex.Message}");
            }
        }

        logSink?.Write(
            $"[AGENT] Web research: provider=Free; " +
            $"resultCount={research.Count}; " +
            $"urls={string.Join(", ", research.Select(item => item.Url))}");
        return research;
    }

    private static string BuildEvidence(
        SearchResult searchResult,
        ExtractedContent extracted)
    {
        var code = extracted.CodeBlocks.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}Code examples:{Environment.NewLine}" +
              string.Join(
                  Environment.NewLine + "---" + Environment.NewLine,
                  extracted.CodeBlocks.Take(10));
        var evidence =
            $"{searchResult.Snippet}{Environment.NewLine}{extracted.Content}{code}"
                .Trim();
        return evidence.Length <= 50000
            ? evidence
            : evidence[..50000];
    }
}
