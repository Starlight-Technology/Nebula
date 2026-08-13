using System.Net;
using System.Text;

using Nebula.Core.Learning;
using Nebula.Services.Learning;

namespace Nebula.Agent.Test;

public sealed class WebResearchServiceTest
{
    [Fact]
    public async Task disabled_provider_returns_expected_error()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DisabledWebResearchService().SearchAsync(
                "Windows commands",
                KnowledgeDomain.WindowsCommands,
                CancellationToken.None));

        Assert.Equal(
            "Web research provider is disabled.",
            exception.Message);
    }

    [Fact]
    public async Task brave_without_api_key_returns_expected_error()
    {
        var service = CreateBraveService(
            new WebResearchOptions
            {
                Provider = "Brave",
                ApiKey = string.Empty
            },
            """{"web":{"results":[]}}""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SearchAsync(
                "Python basics",
                KnowledgeDomain.Python,
                CancellationToken.None));

        Assert.Equal(
            "Brave Search API key is not configured.",
            exception.Message);
    }

    [Fact]
    public async Task brave_mocked_response_returns_research_result()
    {
        var handler = new StubHttpMessageHandler(
            """
            {
              "web": {
                "results": [
                  {
                    "title": "dir command",
                    "url": "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/dir",
                    "description": "Displays a list of files and subdirectories.",
                    "profile": {
                      "long_name": "Microsoft Learn"
                    }
                  }
                ]
              }
            }
            """);
        var service = CreateBraveService(
            new WebResearchOptions
            {
                Provider = "Brave",
                ApiKey = "test-key",
                MaxResults = 5,
                TimeoutSeconds = 20
            },
            handler);

        var results = await service.SearchAsync(
            "dir",
            KnowledgeDomain.WindowsCommands,
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("dir command", result.Title);
        Assert.Contains("Displays a list", result.Snippet);
        Assert.Equal("Microsoft Learn", result.Publisher);
        Assert.Equal(1.0, result.SourceScore);
        Assert.Equal(
            "test-key",
            handler.LastRequest!.Headers
                .GetValues("X-Subscription-Token")
                .Single());
    }

    [Fact]
    public void source_score_recognizes_microsoft_learn()
    {
        var score = WebResearchSourceScorer.Score(
            "https://learn.microsoft.com/en-us/dotnet/core/tools/");

        Assert.Equal(1.0, score);
    }

    [Fact]
    public async Task windows_query_prioritizes_microsoft_learn()
    {
        var handler = new StubHttpMessageHandler(
            """{"web":{"results":[]}}""");
        var service = CreateBraveService(
            ValidOptions(),
            handler);

        await service.SearchAsync(
            "basic commands",
            KnowledgeDomain.WindowsCommands,
            CancellationToken.None);

        var query = GetQuery(handler.LastRequest!.RequestUri!);
        Assert.Contains("site:learn.microsoft.com", query);
        Assert.Contains("Windows command line", query);
    }

    [Fact]
    public async Task python_query_prioritizes_python_documentation()
    {
        var handler = new StubHttpMessageHandler(
            """{"web":{"results":[]}}""");
        var service = CreateBraveService(
            ValidOptions(),
            handler);

        await service.SearchAsync(
            "basic examples",
            KnowledgeDomain.Python,
            CancellationToken.None);

        var query = GetQuery(handler.LastRequest!.RequestUri!);
        Assert.Contains("site:docs.python.org", query);
        Assert.Contains("Python print json", query);
    }

    private static WebResearchOptions ValidOptions() =>
        new()
        {
            Provider = "Brave",
            ApiKey = "test-key",
            MaxResults = 5,
            TimeoutSeconds = 20
        };

    private static BraveWebResearchService CreateBraveService(
        WebResearchOptions options,
        string responseJson) =>
        CreateBraveService(
            options,
            new StubHttpMessageHandler(responseJson));

    private static BraveWebResearchService CreateBraveService(
        WebResearchOptions options,
        HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            options,
            new WebResearchLogSink(_ => { }));

    private static string GetQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Single(part => part.StartsWith("q=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(query[2..]);
    }

    private sealed class StubHttpMessageHandler(
        string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseJson,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
