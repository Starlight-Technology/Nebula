using System.Net;
using System.Text;

using Nebula.Agent.Application;
using Nebula.Core.Learning;
using Nebula.Services.Learning;

namespace Nebula.Agent.Test;

public sealed class FreeWebResearchPipelineTest
{
    [Fact]
    public async Task direct_documentation_prioritizes_powershell_official_docs()
    {
        var results = await new DirectDocumentationProvider().SearchAsync(
            "learn PowerShell Get-ChildItem",
            CancellationToken.None);

        var result = results.First();
        Assert.StartsWith(
            "https://learn.microsoft.com/powershell/module/",
            result.Url);
        Assert.Equal(1.0, result.SearchScore);
    }

    [Theory]
    [InlineData("official physics documentation", "wikipedia.org/wiki/Physics")]
    [InlineData("official chemistry documentation", "wikipedia.org/wiki/Chemistry")]
    [InlineData("official mathematics formula", "wikipedia.org/wiki/Mathematics")]
    public async Task direct_references_cover_non_executable_learning_domains(
        string query,
        string expectedUrl)
    {
        var results = await new DirectDocumentationProvider().SearchAsync(
            query,
            CancellationToken.None);

        Assert.Contains(
            results,
            result => result.Url.Contains(
                expectedUrl,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void bing_html_parser_extracts_title_url_and_snippet()
    {
        var results = BingHtmlSearchProvider.Parse(
            """
            <html><body>
              <ol>
                <li class="b_algo">
                  <h2><a href="https://docs.python.org/3/library/json.html">json</a></h2>
                  <div class="b_caption"><p>JSON encoder and decoder.</p></div>
                </li>
              </ol>
            </body></html>
            """,
            5);

        var result = Assert.Single(results);
        Assert.Equal("json", result.Title);
        Assert.Equal(
            "https://docs.python.org/3/library/json.html",
            result.Url);
        Assert.Equal("JSON encoder and decoder.", result.Snippet);
    }

    [Fact]
    public void content_extractor_removes_navigation_and_keeps_code()
    {
        var extracted = new HtmlContentExtractor().Extract(
            new PageContent(
                "https://docs.example.com/topic",
                """
                <html>
                  <head><title>Official topic</title></head>
                  <body>
                    <nav>Menu that must disappear</nav>
                    <main>
                      <h1>Get-ChildItem</h1>
                      <p>Lists files and directories.</p>
                      <pre>Get-ChildItem -LiteralPath 'C:\'</pre>
                    </main>
                    <footer>Footer that must disappear</footer>
                  </body>
                </html>
                """,
                DateTimeOffset.UtcNow));

        Assert.Equal("Official topic", extracted.Title);
        Assert.Contains("Lists files and directories.", extracted.Content);
        Assert.DoesNotContain("Menu that must disappear", extracted.Content);
        Assert.DoesNotContain("Footer that must disappear", extracted.Content);
        Assert.Contains(
            "Get-ChildItem -LiteralPath",
            Assert.Single(extracted.CodeBlocks));
    }

    [Fact]
    public async Task page_fetcher_uses_cache_without_second_download()
    {
        var handler = new CountingHttpMessageHandler(
            "<html><body><main>cached page</main></body></html>");
        var options = new WebResearchOptions
        {
            CacheDays = 7,
            TimeoutSeconds = 5,
            RateLimitMilliseconds = 100
        };
        var fetcher = new CachedPageFetcher(
            new HttpClient(handler),
            new InMemoryFetchedPageCache(),
            new DomainRateLimiter(options),
            options);

        var first = await fetcher.FetchAsync(
            "https://docs.example.com/topic",
            CancellationToken.None);
        var second = await fetcher.FetchAsync(
            "https://docs.example.com/topic",
            CancellationToken.None);

        Assert.Equal(first.Html, second.Html);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task knowledge_query_returns_source_score_date_and_verification()
    {
        var store = new InMemoryKnowledgeStore();
        var item = new KnowledgeItem
        {
            Domain = KnowledgeDomain.PowerShell,
            Kind = KnowledgeItemKind.Command,
            Topic = "Get-ChildItem",
            Title = "Get-ChildItem",
            Content = "Lists files and directories.",
            Summary = "Get-ChildItem lists files and directories.",
            SourceUrl = "https://learn.microsoft.com/powershell/",
            FinalScore = 0.95,
            UpdatedAt = DateTimeOffset.Parse("2026-06-11T12:00:00Z")
        };
        await store.SaveAsync(
            item,
            [
                new KnowledgeSource
                {
                    KnowledgeItemId = item.Id,
                    Url = item.SourceUrl,
                    Title = item.Title,
                    TrustScore = 1
                }
            ],
            [
                new KnowledgeFact
                {
                    KnowledgeItemId = item.Id,
                    Fact = "Get-ChildItem lists files and directories.",
                    Confidence = 0.98,
                    SourceUrl = item.SourceUrl
                }
            ],
            new KnowledgeExperiment
            {
                KnowledgeItemId = item.Id,
                VerificationKind = VerificationKind.SafeExecution,
                Success = true
            });
        var logger = new Moq.Mock<ILogger>();

        var response = await new KnowledgeQueryService(
            store,
            logger.Object).AnswerAsync(
                "Get-ChildItem",
                CancellationToken.None);

        Assert.Contains(item.SourceUrl, response);
        Assert.Contains("Score: 0.95", response);
        Assert.Contains("2026-06-11", response);
        Assert.Contains("SafeExecution", response);
    }

    private sealed class CountingHttpMessageHandler(
        string html) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    html,
                    Encoding.UTF8,
                    "text/html")
            });
        }
    }
}
