using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Nebula.Agent.Infrastructure;
using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class PullRequestServiceTest
{
    [Fact]
    public void options_drive_provider_configuration_and_default_branch()
    {
        var service = CreateService(new PullRequestOptions
        {
            GitHubToken = "github-token",
            DefaultTargetBranch = "develop"
        });

        Assert.True(service.IsProviderConfigured(PullRequestProvider.GitHub));
        Assert.False(service.IsProviderConfigured(PullRequestProvider.GitLab));
        Assert.True(service.IsProviderConfigured(PullRequestProvider.Auto));
        Assert.Equal("develop", service.DefaultTargetBranch);
    }

    [Fact]
    public async Task create_rejects_invalid_source_branch_before_running_git()
    {
        using var workspace = new TempTestWorkspace();
        var executor = new FakeShellExecutor();
        var service = CreateService(
            new PullRequestOptions { GitHubToken = "token" },
            executor);

        var result = await service.CreatePullRequestAsync(new PullRequestRequest(
            workspace.Path,
            "Title",
            "Description",
            "../feature",
            "main",
            PullRequestProvider.GitHub));

        Assert.False(result.Success);
        Assert.Contains("Source branch", result.Error);
        Assert.Empty(executor.Commands);
    }

    [Fact]
    public async Task create_reports_missing_server_token()
    {
        using var workspace = new TempTestWorkspace();
        var executor = new FakeShellExecutor(command => command switch
        {
            "git remote get-url origin" =>
                ("git@github.com:owner/repo.git", 0, string.Empty),
            _ => (string.Empty, 0, string.Empty)
        });
        var service = CreateService(new PullRequestOptions(), executor);

        var result = await service.CreatePullRequestAsync(new PullRequestRequest(
            workspace.Path,
            "Title",
            "Description",
            "feature/change",
            "main",
            PullRequestProvider.Auto));

        Assert.False(result.Success);
        Assert.Contains("GitHub is not configured", result.Error);
        Assert.Contains("Nebula:PullRequest:GitHubToken", result.Error);
        Assert.DoesNotContain(
            executor.Commands,
            command => command.StartsWith("git push"));
    }

    [Theory]
    [InlineData(
        "git@github.com:owner/repo.git",
        "owner",
        "repo",
        "GitHub")]
    [InlineData(
        "https://github.com/owner/legit.git",
        "owner",
        "legit",
        "GitHub")]
    [InlineData(
        "git@gitlab.com:group/project.git",
        "group",
        "project",
        "GitLab")]
    public async Task repository_info_parses_remote_urls(
        string remoteUrl,
        string owner,
        string name,
        string provider)
    {
        using var workspace = new TempTestWorkspace();
        var executor = new FakeShellExecutor(command => command switch
        {
            "git remote get-url origin" => (remoteUrl, 0, string.Empty),
            _ => (string.Empty, 0, string.Empty)
        });
        var service = CreateService(new PullRequestOptions(), executor);

        var info = await service.GetRepositoryInfoAsync(workspace.Path);

        Assert.True(info.IsRepository);
        Assert.Equal(owner, info.Owner);
        Assert.Equal(name, info.Name);
        Assert.Equal(provider, info.Provider);
    }

    [Fact]
    public async Task create_does_not_push_when_automatic_push_is_disabled()
    {
        using var workspace = new TempTestWorkspace();
        var executor = new FakeShellExecutor(command => command switch
        {
            "git remote get-url origin" =>
                ("git@github.com:owner/repo.git", 0, string.Empty),
            "git status --porcelain" => (string.Empty, 0, string.Empty),
            "git rev-parse --verify origin/feature/change" =>
                (string.Empty, 1, "missing"),
            _ => (string.Empty, 0, string.Empty)
        });
        var service = CreateService(
            new PullRequestOptions
            {
                GitHubToken = "token",
                PushBranchIfNeeded = false
            },
            executor);

        var result = await service.CreatePullRequestAsync(new PullRequestRequest(
            workspace.Path,
            "Title",
            "Description",
            "feature/change",
            "main",
            PullRequestProvider.Auto));

        Assert.False(result.Success);
        Assert.Contains("automatic push is disabled", result.Error);
        Assert.DoesNotContain(
            executor.Commands,
            command => command.StartsWith("git push"));
    }

    [Fact]
    public async Task create_posts_github_pull_request_with_server_token()
    {
        using var workspace = new TempTestWorkspace();
        var executor = new FakeShellExecutor(command => command switch
        {
            "git remote get-url origin" =>
                ("git@github.com:owner/repo.git", 0, string.Empty),
            "git status --porcelain" => (string.Empty, 0, string.Empty),
            "git rev-parse --verify origin/feature/change" =>
                (string.Empty, 0, string.Empty),
            _ => (string.Empty, 0, string.Empty)
        });
        var handler = new RecordingHttpMessageHandler(
            """{"html_url":"https://github.com/owner/repo/pull/7","number":7}""");
        var service = CreateService(
            new PullRequestOptions { GitHubToken = "server-token" },
            executor,
            handler);

        var result = await service.CreatePullRequestAsync(new PullRequestRequest(
            workspace.Path,
            "Selective revert",
            "Adds a safe file revert flow.",
            "feature/change",
            "main",
            PullRequestProvider.Auto));

        Assert.True(result.Success);
        Assert.Equal("https://github.com/owner/repo/pull/7", result.PullRequestUrl);
        Assert.Equal(7, result.PullRequestNumber);
        Assert.Equal(
            "https://api.github.com/repos/owner/repo/pulls",
            handler.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("server-token", handler.AuthorizationParameter);
        Assert.Contains("\"head\":\"feature/change\"", handler.RequestBody);
        Assert.Contains("\"base\":\"main\"", handler.RequestBody);
        Assert.DoesNotContain("server-token", handler.RequestBody);
    }

    [Fact]
    public async Task create_posts_gitlab_merge_request_with_encoded_project_path()
    {
        using var workspace = new TempTestWorkspace();
        var executor = new FakeShellExecutor(command => command switch
        {
            "git remote get-url origin" =>
                ("git@gitlab.com:group/project.git", 0, string.Empty),
            "git status --porcelain" => (string.Empty, 0, string.Empty),
            "git rev-parse --verify origin/feature/change" =>
                (string.Empty, 0, string.Empty),
            _ => (string.Empty, 0, string.Empty)
        });
        var handler = new RecordingHttpMessageHandler(
            """{"web_url":"https://gitlab.com/group/project/-/merge_requests/3","iid":3}""");
        var service = CreateService(
            new PullRequestOptions { GitLabToken = "gitlab-token" },
            executor,
            handler);

        var result = await service.CreatePullRequestAsync(new PullRequestRequest(
            workspace.Path,
            "Selective revert",
            "Adds a safe file revert flow.",
            "feature/change",
            "main",
            PullRequestProvider.Auto));

        Assert.True(result.Success);
        Assert.Equal(
            "https://gitlab.com/group/project/-/merge_requests/3",
            result.PullRequestUrl);
        Assert.Equal(3, result.PullRequestNumber);
        Assert.Equal(
            "https://gitlab.com/api/v4/projects/group%2Fproject/merge_requests",
            handler.RequestUri?.ToString());
        Assert.Equal("gitlab-token", handler.PrivateToken);
        Assert.Contains("\"source_branch\":\"feature/change\"", handler.RequestBody);
        Assert.Contains("\"target_branch\":\"main\"", handler.RequestBody);
        Assert.DoesNotContain("gitlab-token", handler.RequestBody);
    }

    private static PullRequestService CreateService(
        PullRequestOptions options,
        IShellExecutor? executor = null,
        HttpMessageHandler? handler = null)
    {
        return new PullRequestService(
            executor ?? new FakeShellExecutor(),
            new TestLogger(),
            new FakeHttpClientFactory(
                handler ?? new RecordingHttpMessageHandler("{}")),
            Options.Create(options));
    }

    private sealed class FakeShellExecutor : IShellExecutor, IDetailedShellExecutor
    {
        private readonly Func<string, (string Output, int ExitCode, string Error)> handler;

        public FakeShellExecutor(
            Func<string, (string Output, int ExitCode, string Error)>? handler = null)
        {
            this.handler = handler ?? (_ => (string.Empty, 0, string.Empty));
        }

        public List<string> Commands { get; } = [];

        public Task<string> RunCommandAsync(string command)
        {
            return Task.FromResult(handler(command).Output);
        }

        public Task<string> RunCommandAsync(
            string command,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(command).Output);
        }

        public Task<ShellCommandResult> RunCommandDetailedAsync(
            string command,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            var result = handler(command);
            return Task.FromResult(new ShellCommandResult
            {
                Command = command,
                WorkingDirectory = workingDirectory,
                StandardOutput = result.Output,
                StandardError = result.Error,
                ExitCode = result.ExitCode
            });
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class RecordingHttpMessageHandler(string responseBody)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? PrivateToken { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            PrivateToken = request.Headers.TryGetValues(
                "PRIVATE-TOKEN",
                out var privateToken)
                ? privateToken.SingleOrDefault()
                : null;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json")
            };
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
