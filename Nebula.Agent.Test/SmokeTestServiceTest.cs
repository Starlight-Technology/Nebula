using System.Net;
using Moq;
using Nebula.Agent.Infrastructure;
using Nebula.Core.Agent;

namespace Nebula.Agent.Test;

public sealed class SmokeTestServiceTest
{
    [Fact]
    public async Task run_smoke_tests_must_report_healthy_for_successful_response()
    {
        var service = CreateService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await service.RunSmokeTestsAsync(
            new SmokeTestRequest(
                Path.GetTempPath(),
                ["http://localhost:5166"],
                TimeoutSeconds: 5));

        Assert.True(result.Success);
        var check = Assert.Single(result.Checks);
        Assert.Equal(200, check.StatusCode);
        Assert.True(check.Healthy);
    }

    [Fact]
    public async Task run_smoke_tests_must_fail_for_error_response()
    {
        var service = CreateService(
            new StubHttpMessageHandler(
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await service.RunSmokeTestsAsync(
            new SmokeTestRequest(
                Path.GetTempPath(),
                ["http://localhost:5166"],
                TimeoutSeconds: 5));

        Assert.False(result.Success);
        var check = Assert.Single(result.Checks);
        Assert.Equal(500, check.StatusCode);
        Assert.False(check.Healthy);
    }

    [Fact]
    public async Task detect_urls_must_exclude_framework_defaults_when_disabled()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(
            Path.Combine(workspace.Path, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Directory.CreateDirectory(
            Path.Combine(workspace.Path, "Properties"));
        File.WriteAllText(
            Path.Combine(workspace.Path, "Properties", "launchSettings.json"),
            """
            {
              "profiles": {
                "http": {
                  "applicationUrl": "http://localhost:6123"
                }
              }
            }
            """);

        var service = CreateService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var explicitOnly = await service.DetectWebAppUrlsAsync(
            workspace.Path,
            includeFrameworkDefaults: false);
        var withDefaults = await service.DetectWebAppUrlsAsync(
            workspace.Path,
            includeFrameworkDefaults: true);

        Assert.Contains("http://localhost:6123", explicitOnly);
        Assert.DoesNotContain("http://localhost:5166", explicitOnly);
        Assert.Contains("http://localhost:5166", withDefaults);
    }

    private static SmokeTestService CreateService(
        HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(value => value.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new SmokeTestService(
            new TestLogger(),
            factory.Object);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class TestLogger : Nebula.Agent.ILogger
    {
        public void Log(string message)
        {
        }

        public void LogError(string message)
        {
        }
    }
}
