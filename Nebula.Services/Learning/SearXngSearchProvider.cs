using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using Nebula.Core.Learning;

namespace Nebula.Services.Learning;

public sealed class SearXngSearchProvider(
    HttpClient httpClient,
    IOptions<SearXngSearchOptions> optionsAccessor,
    WebResearchLogSink? logSink = null) : ISearchProvider
{
    public const string ProviderName = "SearXNG";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var options = optionsAccessor.Value;
        if (!options.Enabled)
        {
            Log("enabled=false; skipped=true");
            return [];
        }

        if (!TryBuildSearchUri(query, options, out var requestUri))
        {
            Log($"baseUrlInvalid=true; baseUrl={options.BaseUrl}");
            return [];
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(
            TimeSpan.FromSeconds(
                Math.Clamp(options.TimeoutSeconds, 1, 120)));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                Log(
                    $"httpStatus={(int)response.StatusCode}; " +
                    $"reason={response.ReasonPhrase}; " +
                    $"elapsedMs={stopwatch.ElapsedMilliseconds}");
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                timeout.Token);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: timeout.Token);
            var results = ParseResults(
                document.RootElement,
                Math.Clamp(options.MaxResults, 1, 50));

            Log(
                $"resultCount={results.Count}; " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}; " +
                $"urls={string.Join(", ", results.Select(result => result.Url))}");
            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            Log(
                $"error={ex.Message}; elapsedMs={stopwatch.ElapsedMilliseconds}");
            return [];
        }
    }

    public static IReadOnlyList<SearchResult> ParseResults(
        JsonElement root,
        int maxResults)
    {
        if (!root.TryGetProperty("results", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SearchResult>();
        foreach (var value in values.EnumerateArray())
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            var title = Clean(ReadString(value, "title"));
            var url = ReadString(value, "url");
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp &&
                uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            var snippet = Clean(
                ReadString(value, "content") ??
                ReadString(value, "snippet") ??
                ReadString(value, "description"));
            var score = ReadScore(value) ??
                Math.Max(0.1, 1.0 - results.Count * 0.05);

            results.Add(new SearchResult(
                title,
                url,
                snippet,
                Math.Clamp(score, 0, 1)));
        }

        return results;
    }

    private static bool TryBuildSearchUri(
        string query,
        SearXngSearchOptions options,
        out Uri requestUri)
    {
        requestUri = null!;
        if (!Uri.TryCreate(EnsureTrailingSlash(options.BaseUrl), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttp &&
            baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var endpoint = new Uri(baseUri, "search");
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("q", query.Trim()),
            new("format", "json"),
            new("categories", string.IsNullOrWhiteSpace(options.Categories)
                ? "general"
                : options.Categories.Trim()),
            new("safesearch", Math.Clamp(options.SafeSearch, 0, 2)
                .ToString(CultureInfo.InvariantCulture))
        };

        if (!string.IsNullOrWhiteSpace(options.Language))
        {
            parameters.Add(new("language", options.Language.Trim()));
        }

        requestUri = new Uri($"{endpoint}?{BuildQueryString(parameters)}");
        return true;
    }

    private static string EnsureTrailingSlash(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal)
            ? trimmed
            : $"{trimmed}/";
    }

    private static string BuildQueryString(
        IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join(
            '&',
            parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

    private static string? ReadString(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadScore(JsonElement element)
    {
        if (!element.TryGetProperty("score", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var score))
        {
            return score;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(
                   value.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var parsed)
            ? parsed
            : null;
    }

    private static string Clean(string? value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty);
        var withoutTags = Regex.Replace(decoded, "<.*?>", " ");
        return Regex.Replace(withoutTags, @"\s+", " ").Trim();
    }

    private void Log(string message)
    {
        logSink?.Write($"[AGENT] Web research: provider={ProviderName}; {message}");
    }
}
