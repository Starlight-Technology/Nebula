using System.Net.Http.Json;
using System.Text.Json;

using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Core.Sandbox;

namespace Nebula.Runner;

public sealed class SandboxSidecarCommandSandbox : ICommandSandbox, IDisposable
{
    public const string TokenHeader = "X-Nebula-Sandbox-Token";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly NebulaRuntimeSettings settings;
    private readonly string endpoint;
    private readonly string token;
    private readonly bool ownsHttpClient;

    public SandboxSidecarCommandSandbox(
        NebulaRuntimeSettings settings,
        string sidecarUrl,
        string? token = null,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(sidecarUrl))
        {
            throw new ArgumentException(
                "Sandbox sidecar URL is required.",
                nameof(sidecarUrl));
        }

        this.settings = settings;
        this.token = token?.Trim() ?? string.Empty;
        endpoint = $"{sidecarUrl.TrimEnd('/')}/run";
        ownsHttpClient = httpClient is null;
        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public SandboxMode Mode => settings.SandboxMode;

    public bool IsEligible(ShellKind shellKind) =>
        shellKind is ShellKind.PowerShell or ShellKind.Bash or ShellKind.Sh;

    public async Task<ShellCommandResult> RunSandboxedAsync(
        ShellKind shellKind,
        ResolvedCommand command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var request = new SandboxRunRequest(
            shellKind,
            SandboxCommandRequest.From(command),
            workingDirectory,
            settings.SandboxImage,
            settings.SandboxMemoryLimitMb,
            settings.SandboxCpuLimit,
            TimeoutSeconds: 0);

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        if (token.Length > 0)
        {
            message.Headers.TryAddWithoutValidation(TokenHeader, token);
        }

        using var response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Sandbox sidecar failed ({(int)response.StatusCode}): {body}");
        }

        var result = JsonSerializer.Deserialize<SandboxRunResponse>(
            body,
            JsonOptions) ??
            throw new InvalidOperationException(
                "Sandbox sidecar returned an empty response.");

        return new ShellCommandResult
        {
            Command = $"{command.DisplayCommand} (sandbox {result.Runtime})",
            WorkingDirectory = workingDirectory,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            ExitCode = result.ExitCode,
            Timestamp = result.Timestamp
        };
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }
}
