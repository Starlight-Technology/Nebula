using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Sandbox;
using Nebula.Runner;
using Nebula.Sandbox;

namespace Nebula.Agent.Test.Sandbox;

public sealed class SandboxApiTest
{
    [Fact]
    public async Task run_must_reject_missing_token()
    {
        using var fixture = new ApiFixture();
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/run",
            CreateRequest(fixture.Workspace));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(fixture.CommandExecutor.LastCommand);
    }

    [Fact]
    public async Task run_must_reject_wrong_token()
    {
        using var fixture = new ApiFixture();
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/run")
        {
            Content = JsonContent.Create(CreateRequest(fixture.Workspace))
        };
        request.Headers.TryAddWithoutValidation(
            "X-Nebula-Sandbox-Token",
            "wrong-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(fixture.CommandExecutor.LastCommand);
    }

    [Fact]
    public async Task run_must_execute_and_return_result()
    {
        using var fixture = new ApiFixture();
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/run")
        {
            Content = JsonContent.Create(CreateRequest(fixture.Workspace))
        };
        request.Headers.TryAddWithoutValidation(
            "X-Nebula-Sandbox-Token",
            "test-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SandboxRunResponse>();
        Assert.NotNull(result);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("podman", result.Runtime);
        Assert.NotNull(fixture.CommandExecutor.LastCommand);
        Assert.Contains(
            $"\"{fixture.HostWorkspace}:/workspace:rw,Z\"",
            fixture.CommandExecutor.LastCommand.Arguments);
    }

    [Fact]
    public async Task run_must_reject_workspace_outside_container_root()
    {
        using var fixture = new ApiFixture();
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/run")
        {
            Content = JsonContent.Create(
                CreateRequest(
                    Path.Combine(
                        fixture.ContainerRoot,
                        "..",
                        "outside-workspace")))
        };
        request.Headers.TryAddWithoutValidation(
            "X-Nebula-Sandbox-Token",
            "test-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(fixture.CommandExecutor.LastCommand);
    }

    [Fact]
    public async Task run_must_reject_invalid_image()
    {
        using var fixture = new ApiFixture();
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/run")
        {
            Content = JsonContent.Create(
                CreateRequest(fixture.Workspace, image: "-invalid image"))
        };
        request.Headers.TryAddWithoutValidation(
            "X-Nebula-Sandbox-Token",
            "test-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(fixture.CommandExecutor.LastCommand);
    }

    [Fact]
    public async Task health_must_fail_when_token_is_not_configured()
    {
        using var fixture = new ApiFixture(token: string.Empty);
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task health_must_fail_when_runtime_is_unavailable()
    {
        using var fixture = new ApiFixture(runtimeAvailable: false);
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task health_must_pass_when_runtime_is_available()
    {
        using var fixture = new ApiFixture();
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static SandboxRunRequest CreateRequest(
        string workspace,
        string image = "mcr.microsoft.com/dotnet/sdk:10.0-noble") =>
        new(
            ShellKind.Bash,
            new SandboxCommandRequest(
                "bash",
                "-c \"echo hi\"",
                "echo hi",
                workspace,
                []),
            workspace,
            image,
            0,
            0,
            30);

    private sealed class ApiFixture : IDisposable
    {
        public ApiFixture(
            string token = "test-token",
            bool runtimeAvailable = true)
        {
            ContainerRoot = CreateDirectory("container");
            HostRoot = CreateDirectory("host");
            Workspace = Directory.CreateDirectory(
                Path.Combine(ContainerRoot, "workspace")).FullName;
            HostWorkspace = Path.Combine(
                HostRoot,
                Path.GetRelativePath(ContainerRoot, Workspace));
            CommandExecutor = new CapturingExecutor();
            Factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("SANDBOX_TOKEN", token);
                    builder.UseSetting(
                        "SANDBOX_CONTAINER_TEMP_ROOT",
                        ContainerRoot);
                    builder.UseSetting("SANDBOX_HOST_TEMP_ROOT", HostRoot);
                    builder.UseSetting("SANDBOX_RUNTIME", "Podman");
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<ISandboxRuntimeLocator>();
                        services.AddSingleton<ISandboxRuntimeLocator>(
                            new FakeRuntimeLocator(
                                runtimeAvailable
                                    ? new SandboxRuntimeInfo(
                                        "podman",
                                        "podman",
                                        true)
                                    : null));
                        services.RemoveAll<IResolvedCommandExecutor>();
                        services.AddSingleton<IResolvedCommandExecutor>(
                            CommandExecutor);
                    });
                });
        }

        public string ContainerRoot { get; }

        public string HostRoot { get; }

        public string Workspace { get; }

        public string HostWorkspace { get; }

        public CapturingExecutor CommandExecutor { get; }

        public WebApplicationFactory<Program> Factory { get; }

        public void Dispose()
        {
            Factory.Dispose();
            TryDelete(ContainerRoot);
            TryDelete(HostRoot);
        }

        private static string CreateDirectory(string prefix) =>
            Directory.CreateDirectory(
                Path.Combine(
                    Path.GetTempPath(),
                    $"nebula-sandbox-api-{prefix}-{Guid.NewGuid():N}"))
                .FullName;

        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class CapturingExecutor : IResolvedCommandExecutor
    {
        public ResolvedCommand? LastCommand { get; private set; }

        public Task<string> RunCommandAsync(string command) =>
            throw new NotSupportedException();

        public Task<string> RunCommandAsync(
            string command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ShellCommandResult> RunCommandDetailedAsync(
            string command,
            string workingDirectory,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ShellCommandResult> RunCommandDetailedAsync(
            ResolvedCommand command,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult(new ShellCommandResult
            {
                Command = command.DisplayCommand,
                WorkingDirectory = command.WorkingDirectory,
                StandardOutput = "ok",
                ExitCode = 0
            });
        }
    }

    private sealed class FakeRuntimeLocator(
        SandboxRuntimeInfo? runtime) : ISandboxRuntimeLocator
    {
        public SandboxRuntimeInfo? Locate(SandboxContainerRuntime preference) =>
            runtime;
    }
}
