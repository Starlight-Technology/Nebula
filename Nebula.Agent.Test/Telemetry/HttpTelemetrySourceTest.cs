using System.Net;
using System.Text;

using Nebula.Llama.Client.Telemetry;

namespace Nebula.Agent.Test.Telemetry;

public sealed class HttpTelemetrySourceTest
{
    [Fact]
    public async Task get_snapshot_async_must_deserialize_sidecar_payload()
    {
        const string payload = """
        {
          "capturedAt": "2026-10-24T16:57:46.584973+00:00",
          "source": "sidecar",
          "cpu": {
            "model": "12th Gen Intel(R) Core(TM) i7-12700H",
            "logicalCores": 20,
            "usagePercent": 34.5,
            "detail": "12th Gen Intel(R) Core(TM) i7-12700H | 20 threads"
          },
          "memory": {
            "totalBytes": 24779767808,
            "availableBytes": 4028882944,
            "usedBytes": 20750884864,
            "usagePercent": 83.7,
            "detail": "19.3 GiB / 23.1 GiB (83.7%)"
          },
          "gpus": [
            {
              "vendor": "nvidia",
              "name": "NVIDIA GeForce RTX 3050 Laptop GPU",
              "pciId": "10DE:25A2",
              "driver": "nvidia",
              "source": "proc",
              "isAvailable": true
            }
          ],
          "disks": [
            {
              "name": "Host data",
              "mountPoint": "/host/root/var",
              "fileSystem": "btrfs",
              "totalBytes": 1021440950272,
              "freeBytes": 230021070848,
              "usedBytes": 791419879424,
              "usagePercent": 77.5,
              "isHost": true
            }
          ],
          "warnings": []
        }
        """;

        using var client = new HttpClient(new StubHandler(payload))
        {
            BaseAddress = new Uri("http://sidecar:8090/")
        };
        using var source = new HttpTelemetrySource("http://sidecar:8090", client);

        var snapshot = await source.GetSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.Equal("sidecar", snapshot.Source);
        Assert.Equal(20, snapshot.Cpu.LogicalCores);
        Assert.Contains("i7-12700H", snapshot.Cpu.Detail);
        Assert.Equal("Host data", Assert.Single(snapshot.Disks).Name);
        Assert.Equal("nvidia", Assert.Single(snapshot.Gpus).Vendor);
        Assert.Contains("RTX 3050", Assert.Single(snapshot.Gpus).Name);
    }

    [Fact]
    public async Task get_snapshot_async_must_return_null_when_sidecar_fails()
    {
        using var client = new HttpClient(new StubHandler("erro", HttpStatusCode.InternalServerError))
        {
            BaseAddress = new Uri("http://sidecar:8090/")
        };
        using var source = new HttpTelemetrySource("http://sidecar:8090", client);

        var snapshot = await source.GetSnapshotAsync();

        Assert.Null(snapshot);
    }

    private sealed class StubHandler(string payload, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
