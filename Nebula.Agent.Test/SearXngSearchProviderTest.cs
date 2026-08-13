using System.Net;
using System.Text;

using Microsoft.Extensions.Options;

using Nebula.Core.Learning;
using Nebula.Services.Learning;

namespace Nebula.Agent.Test;

public sealed class SearXngSearchProviderTest
{
    [Fact]
    public async Task searxng_mocked_response_returns_search_result()
    {
        var handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "results": [
                {
                  "title": "PowerShell command safety",
                  "url": "https://learn.microsoft.com/powershell/",
                  "content": "Official guidance for PowerShell.",
                  "score": 0.92
                }
              ]
            }
            """);
        var provider = CreateProvider(handler);

        var results = await provider.SearchAsync(
            "boas praticas PowerShell",
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("PowerShell command safety", result.Title);
        Assert.Equal("https://learn.microsoft.com/powershell/", result.Url);
        Assert.Equal("Official guidance for PowerShell.", result.Snippet);
        Assert.Equal(0.92, result.SearchScore, precision: 2);
        Assert.Contains("format=json", handler.LastRequest!.RequestUri!.Query);
        Assert.Contains("language=pt-BR", handler.LastRequest.RequestUri.Query);
        Assert.Contains("categories=general", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task searxng_returns_empty_when_endpoint_fails()
    {
        var provider = CreateProvider(
            new StubHttpMessageHandler(
                HttpStatusCode.ServiceUnavailable,
                "{}"));

        var results = await provider.SearchAsync(
            "dotnet HttpClientFactory",
            CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task searxng_disabled_returns_empty_without_request()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var provider = CreateProvider(
            handler,
            new SearXngSearchOptions { Enabled = false });

        var results = await provider.SearchAsync(
            "dotnet",
            CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task web_search_orchestrator_deduplicates_urls_by_score()
    {
        var orchestrator = new WebSearchOrchestrator(
            [
                new FakeSearchProvider(
                    new SearchResult(
                        "First",
                        "https://docs.example.com/topic#section",
                        "low",
                        0.4)),
                new FakeSearchProvider(
                    new SearchResult(
                        "Second",
                        "https://docs.example.com/topic",
                        "high",
                        0.9),
                    new SearchResult(
                        "Other",
                        "https://docs.example.com/other",
                        "other",
                        0.7))
            ],
            new WebResearchLogSink(_ => { }));

        var results = await orchestrator.SearchAsync(
            "topic",
            10,
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("Second", results[0].Title);
        Assert.Equal("Other", results[1].Title);
    }

    [Fact]
    public async Task web_search_orchestrator_continues_when_provider_fails()
    {
        var orchestrator = new WebSearchOrchestrator(
            [
                new ThrowingSearchProvider(),
                new FakeSearchProvider(
                    new SearchResult(
                        "Recovered",
                        "https://docs.example.com/recovered",
                        "ok",
                        0.8))
            ],
            new WebResearchLogSink(_ => { }));

        var result = Assert.Single(await orchestrator.SearchAsync(
            "topic",
            10,
            CancellationToken.None));

        Assert.Equal("Recovered", result.Title);
    }

    private static SearXngSearchProvider CreateProvider(
        HttpMessageHandler handler,
        SearXngSearchOptions? options = null) =>
        new(
            new HttpClient(handler),
            Options.Create(options ?? new SearXngSearchOptions
            {
                Enabled = true,
                BaseUrl = "http://localhost:8080",
                MaxResults = 10,
                TimeoutSeconds = 20,
                Language = "pt-BR",
                SafeSearch = 1
            }),
            new WebResearchLogSink(_ => { }));

    private sealed class StubHttpMessageHandler(
        HttpStatusCode statusCode,
        string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseJson,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class FakeSearchProvider(
        params SearchResult[] results) : ISearchProvider
    {
        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            string query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    private sealed class ThrowingSearchProvider : ISearchProvider
    {
        public Task<IReadOnlyList<SearchResult>> SearchAsync(
            string query,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
