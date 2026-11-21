using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Core.Sandbox;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class SandboxSidecarCommandSandboxTest
{
    [Fact]
    public async Task run_must_send_request_and_map_response()
    {
        SandboxRunRequest? capturedRequest = null;
        string? capturedToken = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            capturedToken = request.Headers
                .TryGetValues(
                    SandboxSidecarCommandSandbox.TokenHeader,
                    out var values)
                ? values.FirstOrDefault()
                : null;
            capturedRequest = await request.Content!.ReadFromJsonAsync<SandboxRunRequest>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SandboxRunResponse(
                    0,
                    "hello from sidecar",
                    string.Empty,
                    "echo oi (sandbox podman)",
                    "/tmp/nebula-workspace/abc",
                    DateTimeOffset.UtcNow,
                    "podman"),
                    options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
        });
        var settings = new NebulaRuntimeSettings
        {
            SandboxMode = SandboxMode.Docker,
            SandboxImage = "mcr.microsoft.com/powershell:lts",
            SandboxMemoryLimitMb = 256,
            SandboxCpuLimit = 0.5
        };
        using var httpClient = new HttpClient(handler);
        using var sandbox = new SandboxSidecarCommandSandbox(
            settings,
            "http://nebula-sandbox:8091/",
            "secret-token",
            httpClient);
        var workspace = "/tmp/nebula-workspace/abc";
        var command = new ResolvedCommand(
            "bash",
            "-c \"echo oi\"",
            "echo oi",
            workspace,
            []);

        var result = await sandbox.RunSandboxedAsync(
            ShellKind.Bash,
            command,
            workspace,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello from sidecar", result.StandardOutput);
        Assert.Contains("(sandbox podman)", result.Command);
        Assert.Equal("secret-token", capturedToken);
        Assert.NotNull(capturedRequest);
        Assert.Equal(ShellKind.Bash, capturedRequest.ShellKind);
        Assert.Equal(workspace, capturedRequest.WorkspacePath);
        Assert.Equal(settings.SandboxImage, capturedRequest.Image);
        Assert.Equal(256, capturedRequest.MemoryLimitMb);
        Assert.Equal(0.5, capturedRequest.CpuLimit);
        Assert.Equal("echo oi", capturedRequest.Command.DisplayCommand);
    }

    [Fact]
    public async Task run_must_throw_with_sidecar_error_body()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("runtime unavailable")
            }));
        var settings = new NebulaRuntimeSettings
        {
            SandboxMode = SandboxMode.Docker
        };
        using var httpClient = new HttpClient(handler);
        using var sandbox = new SandboxSidecarCommandSandbox(
            settings,
            "http://nebula-sandbox:8091",
            "secret-token",
            httpClient);
        var command = new ResolvedCommand(
            "bash",
            "-c \"echo oi\"",
            "echo oi",
            "/tmp/nebula-workspace/abc",
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.RunSandboxedAsync(
                ShellKind.Bash,
                command,
                "/tmp/nebula-workspace/abc",
                CancellationToken.None));

        Assert.Contains("503", exception.Message);
        Assert.Contains("runtime unavailable", exception.Message);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request);
    }
}
