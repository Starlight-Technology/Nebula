using System.Net.Http.Json;
using System.Text.Json;

using Nebula.Core.Telemetry;

namespace Nebula.Llama.Client.Telemetry;

public interface ITelemetrySource
{
    Task<HostTelemetrySnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class HttpTelemetrySource : ITelemetrySource, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient client;
    private readonly bool ownsClient;

    public HttpTelemetrySource(string baseUrl, HttpClient? client = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var normalized = baseUrl.TrimEnd('/') + "/";
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException("A URL do sidecar de telemetria e invalida.", nameof(baseUrl));
        }

        this.client = client ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        ownsClient = client is null;
        this.client.BaseAddress = baseUri;
    }

    public async Task<HostTelemetrySnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await client.GetFromJsonAsync<HostTelemetrySnapshot>(
                "snapshot",
                JsonOptions,
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            client.Dispose();
        }
    }
}
