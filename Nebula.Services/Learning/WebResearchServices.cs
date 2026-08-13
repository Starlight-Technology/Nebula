using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

using Nebula.Core.Configuration;
using Nebula.Core.Learning;

namespace Nebula.Services.Learning;

public sealed class ConfigurableWebResearchService(
    NebulaRuntimeSettings runtimeSettings,
    WebResearchOptions configuredOptions,
    FreeWebResearchService freeService,
    BraveWebResearchService braveService,
    DisabledWebResearchService disabledService,
    WebResearchLogSink? logSink = null) : IWebResearchService
{
    public Task<IReadOnlyList<ResearchResult>> SearchAsync(
        string topic,
        KnowledgeDomain domain,
        CancellationToken cancellationToken)
    {
        var provider = string.IsNullOrWhiteSpace(runtimeSettings.WebResearchProvider)
            ? configuredOptions.Provider
            : runtimeSettings.WebResearchProvider;

        return provider.Trim().ToLowerInvariant() switch
        {
            "brave" => braveService.SearchAsync(topic, domain, cancellationToken),
            "free" or "searxng" or "bing" or "binghtml" or "directdocumentation" =>
                freeService.SearchAsync(topic, domain, cancellationToken),
            "disabled" or "" =>
                disabledService.SearchAsync(topic, domain, cancellationToken),
            _ => new UnsupportedWebResearchService(provider, logSink)
                .SearchAsync(topic, domain, cancellationToken)
        };
    }
}

public sealed class DisabledWebResearchService(
    WebResearchLogSink? logSink = null) : IWebResearchService
{
    public Task<IReadOnlyList<ResearchResult>> SearchAsync(
        string topic,
        KnowledgeDomain domain,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logSink?.Write(
            "[AGENT] Web research: provider=Disabled; request rejected.");
        throw new InvalidOperationException(
            "Web research provider is disabled.");
    }
}

public sealed class UnsupportedWebResearchService(
    string provider,
    WebResearchLogSink? logSink = null) : IWebResearchService
{
    public Task<IReadOnlyList<ResearchResult>> SearchAsync(
        string topic,
        KnowledgeDomain domain,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logSink?.Write(
            $"[AGENT] Web research: provider={provider}; provider is not implemented.");
        throw new InvalidOperationException(
            $"Web research provider '{provider}' is not implemented.");
    }
}

public sealed class BraveWebResearchService(
    HttpClient httpClient,
    WebResearchOptions options,
    WebResearchLogSink? logSink = null) : IWebResearchService
{
    private const string SearchEndpoint =
        "https://api.search.brave.com/res/v1/web/search";

    public async Task<IReadOnlyList<ResearchResult>> SearchAsync(
        string topic,
        KnowledgeDomain domain,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            Log("provider=Brave; API key is not configured.");
            throw new InvalidOperationException(
                "Brave Search API key is not configured.");
        }

        var query = WebResearchQueryBuilder.Build(topic, domain);
        var maxResults = Math.Clamp(options.MaxResults, 1, 20);
        var requestUri =
            $"{SearchEndpoint}?q={Uri.EscapeDataString(query)}&count={maxResults}";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            requestUri);
        request.Headers.Add(
            "X-Subscription-Token",
            options.ApiKey.Trim());
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        Log($"provider=Brave; query={query}");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(
                TimeSpan.FromSeconds(
                    Math.Clamp(options.TimeoutSeconds, 1, 120)));

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                Log(
                    $"provider=Brave; httpStatus={(int)response.StatusCode}; " +
                    $"reason={response.ReasonPhrase}; elapsedMs={stopwatch.ElapsedMilliseconds}");
                response.EnsureSuccessStatusCode();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                timeout.Token);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: timeout.Token);
            var results = ParseResults(
                document.RootElement,
                maxResults,
                DateTimeOffset.UtcNow);

            Log(
                $"provider=Brave; resultCount={results.Count}; " +
                $"urls={string.Join(", ", results.Select(result => result.Url))}; " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            return results;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log(
                $"provider=Brave; error={ex.Message}; " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
            throw;
        }
    }

    private static IReadOnlyList<ResearchResult> ParseResults(
        JsonElement root,
        int maxResults,
        DateTimeOffset retrievedAt)
    {
        if (!root.TryGetProperty("web", out var web) ||
            !web.TryGetProperty("results", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ResearchResult>();
        foreach (var value in values.EnumerateArray())
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            var title = ReadString(value, "title");
            var url = ReadString(value, "url");
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                continue;
            }

            var publisher = ReadNestedString(
                    value,
                    "profile",
                    "long_name") ??
                ReadString(value, "publisher");
            results.Add(new ResearchResult(
                title,
                url,
                ReadString(value, "description") ?? string.Empty,
                publisher,
                retrievedAt,
                WebResearchSourceScorer.Score(url)));
        }

        return results;
    }

    private static string? ReadString(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadNestedString(
        JsonElement element,
        string objectName,
        string propertyName)
    {
        return element.TryGetProperty(objectName, out var nested) &&
               nested.ValueKind == JsonValueKind.Object
            ? ReadString(nested, propertyName)
            : null;
    }

    private void Log(string message)
    {
        logSink?.Write($"[AGENT] Web research: {message}");
    }
}

public static class WebResearchQueryBuilder
{
    public static string Build(
        string topic,
        KnowledgeDomain domain)
    {
        var normalizedTopic = topic.Trim();
        var domainPrefix = domain switch
        {
            KnowledgeDomain.WindowsCommands =>
                "site:learn.microsoft.com Windows command line dir mkdir copy official documentation",
            KnowledgeDomain.PowerShell =>
                "site:learn.microsoft.com PowerShell Get-ChildItem official documentation",
            KnowledgeDomain.DotNet =>
                "site:learn.microsoft.com dotnet CLI official documentation",
            KnowledgeDomain.Python =>
                "site:docs.python.org Python print json official documentation",
            KnowledgeDomain.LinuxCommands =>
                "official Linux command documentation",
            KnowledgeDomain.Mathematics =>
                "official documentation math formula examples",
            KnowledgeDomain.Physics =>
                "official physics documentation formulas examples",
            KnowledgeDomain.Chemistry =>
                "official chemistry documentation formulas examples",
            _ => "official documentation"
        };

        return $"{domainPrefix} {normalizedTopic}".Trim();
    }
}

public static class WebResearchSourceScorer
{
    public static double Score(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return 0.4;
        }

        var host = uri.Host.ToLowerInvariant();
        if (HostMatches(host, "learn.microsoft.com") ||
            HostMatches(host, "docs.python.org"))
        {
            return 1.0;
        }

        if (HostMatches(host, "wikipedia.org"))
        {
            return 0.7;
        }

        if (HostMatches(host, "stackoverflow.com"))
        {
            return 0.6;
        }

        if (HostMatches(host, "man7.org") ||
            HostMatches(host, "kubernetes.io") ||
            HostMatches(host, "docs.docker.com") ||
            HostMatches(host, "docs.unity3d.com"))
        {
            return 0.9;
        }

        if (LooksLikeOfficialDocumentation(host, uri.AbsolutePath))
        {
            return 0.9;
        }

        return 0.4;
    }

    private static bool HostMatches(string host, string expected) =>
        host.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(
            $".{expected}",
            StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeOfficialDocumentation(
        string host,
        string path)
    {
        return host.StartsWith("docs.", StringComparison.OrdinalIgnoreCase) ||
               host.StartsWith("developer.", StringComparison.OrdinalIgnoreCase) ||
               path.Contains(
                   "/docs/",
                   StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(
                   ".gov",
                   StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(
                   ".edu",
                   StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class WebResearchLogSink(Action<string> write)
{
    public void Write(string message) => write(message);
}
