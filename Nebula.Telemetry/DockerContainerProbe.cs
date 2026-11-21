using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nebula.Core.Telemetry;

namespace Nebula.Telemetry;

public sealed class DockerContainerProbe : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string socketPath;
    private readonly string containerName;
    private readonly Lazy<HttpClient> client;

    public DockerContainerProbe(string? socketPath, string containerName)
    {
        this.socketPath = socketPath?.Trim() ?? string.Empty;
        this.containerName = string.IsNullOrWhiteSpace(containerName) ? "ollama" : containerName.Trim();
        client = new Lazy<HttpClient>(CreateClient);
    }

    public async Task<OllamaContainerTelemetry?> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(socketPath) || !File.Exists(socketPath))
        {
            return null;
        }

        try
        {
            var inspect = await client.Value.GetFromJsonAsync<DockerContainerInspect>(
                $"/containers/{Uri.EscapeDataString(containerName)}/json",
                JsonOptions,
                cancellationToken);

            if (inspect is null)
            {
                return null;
            }

            var env = ParseEnvironment(inspect.Config?.Env);
            var runtimeMode = ResolveRuntimeMode(env);
            var state = inspect.State?.Status ?? "unknown";
            var isRunning = state.Equals("running", StringComparison.OrdinalIgnoreCase);

            DockerStatsResponse? stats = null;
            try
            {
                stats = await client.Value.GetFromJsonAsync<DockerStatsResponse>(
                    $"/containers/{Uri.EscapeDataString(inspect.Id)}/stats?stream=false",
                    JsonOptions,
                    cancellationToken);
            }
            catch
            {
                // O status e o modo ainda sao uteis mesmo sem amostra de stats.
            }

            var cpuPercent = CalculateCpuPercent(stats);
            var memoryUsed = CalculateMemoryUsed(stats);
            var memoryLimit = stats?.MemoryStats?.Limit;
            var memoryPercent = memoryUsed is not null && memoryLimit is > 0
                ? (double?)(memoryUsed.Value * 100d / memoryLimit.Value)
                : null;

            return new OllamaContainerTelemetry
            {
                IsAvailable = isRunning,
                StatusLabel = isRunning ? "Ollama online" : "Ollama offline",
                StatusDetail = $"Container {containerName}: {state}.",
                ContainerName = containerName,
                CpuPercent = cpuPercent,
                MemoryUsedBytes = memoryUsed,
                MemoryLimitBytes = memoryLimit,
                MemoryPercent = memoryPercent,
                RuntimeModeLabel = runtimeMode.Label,
                RuntimeModeDetail = runtimeMode.Detail,
                Vendor = runtimeMode.Vendor,
                UsesGpu = runtimeMode.UsesGpu
            };
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (client.IsValueCreated)
        {
            client.Value.Dispose();
        }
    }

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://docker"),
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    private static double? CalculateCpuPercent(DockerStatsResponse? stats)
    {
        var currentUsage = stats?.CpuStats?.CpuUsage?.TotalUsage;
        var previousUsage = stats?.PreCpuStats?.CpuUsage?.TotalUsage;
        var currentSystem = stats?.CpuStats?.SystemCpuUsage;
        var previousSystem = stats?.PreCpuStats?.SystemCpuUsage;

        if (currentUsage is null || previousUsage is null || currentSystem is null || previousSystem is null)
        {
            return null;
        }

        var cpuDelta = currentUsage.Value - previousUsage.Value;
        var systemDelta = currentSystem.Value - previousSystem.Value;
        if (cpuDelta <= 0 || systemDelta <= 0)
        {
            return null;
        }

        var onlineCpus = stats?.CpuStats?.OnlineCpus ?? Environment.ProcessorCount;
        return cpuDelta * 100d / systemDelta * Math.Max(1, onlineCpus);
    }

    private static long? CalculateMemoryUsed(DockerStatsResponse? stats)
    {
        var usage = stats?.MemoryStats?.Usage;
        if (usage is null)
        {
            return null;
        }

        var cache = stats?.MemoryStats?.Stats?.Cache ?? 0;
        return Math.Max(0, usage.Value - cache);
    }

    private static Dictionary<string, string> ParseEnvironment(IEnumerable<string>? entries)
    {
        return (entries ?? [])
            .Select(entry => entry.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
    }

    private static RuntimeMode ResolveRuntimeMode(IReadOnlyDictionary<string, string> env)
    {
        if (env.TryGetValue("OLLAMA_ACCELERATION_MODE", out var accelerationMode) &&
            !string.IsNullOrWhiteSpace(accelerationMode))
        {
            return NormalizeRuntimeMode(accelerationMode, env);
        }

        if (env.TryGetValue("OLLAMA_GPU_VENDOR", out var gpuVendor) &&
            !string.IsNullOrWhiteSpace(gpuVendor))
        {
            return NormalizeRuntimeMode(gpuVendor, env);
        }

        if (env.TryGetValue("NVIDIA_VISIBLE_DEVICES", out var nvidiaDevices) &&
            !string.IsNullOrWhiteSpace(nvidiaDevices))
        {
            return new RuntimeMode("NVIDIA CUDA", "Detectado via variaveis NVIDIA do container.", "nvidia", true);
        }

        if (env.TryGetValue("ROCR_VISIBLE_DEVICES", out var rocrDevices) &&
            !string.IsNullOrWhiteSpace(rocrDevices))
        {
            return new RuntimeMode("AMD ROCm", "Detectado via variaveis ROCm do container.", "amd", true);
        }

        if (env.TryGetValue("OLLAMA_VULKAN", out var vulkanEnabled) && vulkanEnabled == "1")
        {
            return new RuntimeMode("Intel Vulkan", "Detectado via backend Vulkan do container.", "intel", true);
        }

        return new RuntimeMode("CPU runtime", "Nenhuma pista de aceleracao foi encontrada no container.", "cpu", false);
    }

    private static RuntimeMode NormalizeRuntimeMode(
        string rawMode,
        IReadOnlyDictionary<string, string> env)
    {
        var normalized = rawMode.Trim().ToLowerInvariant();

        if (normalized.Contains("nvidia") || normalized.Contains("cuda"))
        {
            return new RuntimeMode("NVIDIA CUDA", "Runtime configurado para NVIDIA CUDA.", "nvidia", true);
        }

        if (normalized.Contains("amd") || normalized.Contains("rocm"))
        {
            return new RuntimeMode("AMD ROCm", "Runtime configurado para AMD ROCm.", "amd", true);
        }

        if (normalized.Contains("intel") || normalized.Contains("vulkan"))
        {
            return new RuntimeMode("Intel Vulkan", "Runtime configurado para Intel Vulkan.", "intel", true);
        }

        if (normalized.Contains("cpu"))
        {
            return new RuntimeMode("CPU runtime", "Runtime configurado para CPU.", "cpu", false);
        }

        if (env.TryGetValue("NVIDIA_VISIBLE_DEVICES", out var nvidiaDevices) &&
            !string.IsNullOrWhiteSpace(nvidiaDevices))
        {
            return new RuntimeMode("NVIDIA CUDA", "Detectado via variaveis NVIDIA do container.", "nvidia", true);
        }

        if (env.TryGetValue("ROCR_VISIBLE_DEVICES", out var rocrDevices) &&
            !string.IsNullOrWhiteSpace(rocrDevices))
        {
            return new RuntimeMode("AMD ROCm", "Detectado via variaveis ROCm do container.", "amd", true);
        }

        return new RuntimeMode("CPU runtime", "Nao reconheci o modo de aceleracao retornado pelo container.", "cpu", false);
    }

    private sealed record RuntimeMode(string Label, string Detail, string Vendor, bool UsesGpu);

    private sealed class DockerContainerInspect
    {
        public string Id { get; set; } = string.Empty;

        public DockerContainerConfig? Config { get; set; }

        public DockerContainerState? State { get; set; }
    }

    private sealed class DockerContainerConfig
    {
        public string[] Env { get; set; } = [];
    }

    private sealed class DockerContainerState
    {
        public string Status { get; set; } = string.Empty;
    }

    private sealed class DockerStatsResponse
    {
        public DockerCpuStats? CpuStats { get; set; }

        [JsonPropertyName("precpu_stats")]
        public DockerCpuStats? PreCpuStats { get; set; }

        public DockerMemoryStats? MemoryStats { get; set; }
    }

    private sealed class DockerCpuStats
    {
        public DockerCpuUsage? CpuUsage { get; set; }

        public long? SystemCpuUsage { get; set; }

        public int? OnlineCpus { get; set; }
    }

    private sealed class DockerCpuUsage
    {
        public long? TotalUsage { get; set; }
    }

    private sealed class DockerMemoryStats
    {
        public long? Usage { get; set; }

        public long? Limit { get; set; }

        public DockerMemoryStatsDetails? Stats { get; set; }
    }

    private sealed class DockerMemoryStatsDetails
    {
        public long? Cache { get; set; }
    }
}
