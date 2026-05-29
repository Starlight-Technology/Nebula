using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nebula.Llama.Client;

public sealed class LlamaRuntimeTelemetryService : ILlamaRuntimeTelemetryService
{
    private const string DefaultContainerName = "ollama";
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RuntimeModeTtl = TimeSpan.FromMinutes(1);

    private readonly IRuntimeCommandRunner commandRunner;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string containerName;
    private readonly TimeProvider timeProvider;

    private LlamaRuntimeTelemetrySnapshot? cachedSnapshot;
    private DateTimeOffset cachedSnapshotAt;
    private RuntimeModeSnapshot? cachedRuntimeMode;
    private DateTimeOffset cachedRuntimeModeAt;

    public LlamaRuntimeTelemetryService(
        IRuntimeCommandRunner? commandRunner = null,
        string? containerName = null,
        TimeProvider? timeProvider = null)
    {
        this.commandRunner = commandRunner ?? new ProcessRuntimeCommandRunner();
        this.containerName = string.IsNullOrWhiteSpace(containerName)
            ? Environment.GetEnvironmentVariable("OLLAMA_CONTAINER_NAME") ?? DefaultContainerName
            : containerName.Trim();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LlamaRuntimeTelemetrySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var now = GetNow();
        if (cachedSnapshot is not null && now - cachedSnapshotAt < SnapshotTtl)
        {
            return cachedSnapshot;
        }

        await gate.WaitAsync(cancellationToken);

        try
        {
            now = GetNow();
            if (cachedSnapshot is not null && now - cachedSnapshotAt < SnapshotTtl)
            {
                return cachedSnapshot;
            }

            cachedSnapshot = await BuildSnapshotAsync(cancellationToken);
            cachedSnapshotAt = now;

            return cachedSnapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LlamaRuntimeTelemetrySnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var statsCommand = await commandRunner.RunAsync(
            "docker",
            ["stats", "--no-stream", "--format", "{{json .}}", containerName],
            cancellationToken);

        if (!statsCommand.Succeeded || string.IsNullOrWhiteSpace(statsCommand.StandardOutput))
        {
            var details = string.IsNullOrWhiteSpace(statsCommand.StandardError)
                ? statsCommand.StandardOutput.Trim()
                : statsCommand.StandardError.Trim();

            return new LlamaRuntimeTelemetrySnapshot
            {
                IsAvailable = false,
                ContainerName = containerName,
                StatusLabel = "Ollama offline",
                StatusDetail = string.IsNullOrWhiteSpace(details)
                    ? $"Nao consegui ler o container {containerName} via docker stats."
                    : details,
                RuntimeModeLabel = "Runtime indisponivel",
                RuntimeModeDetail = "O container do Ollama nao respondeu ao docker stats.",
                LastError = string.IsNullOrWhiteSpace(details) ? null : details,
                CapturedAt = GetNow()
            };
        }

        var stats = ParseDockerStats(statsCommand.StandardOutput);
        var runtimeMode = await GetRuntimeModeAsync(cancellationToken);
        var gpuMetric = await GetGpuMetricAsync(runtimeMode, cancellationToken);

        return new LlamaRuntimeTelemetrySnapshot
        {
            IsAvailable = true,
            ContainerName = string.IsNullOrWhiteSpace(stats.Name) ? containerName : stats.Name,
            StatusLabel = "Ollama online",
            StatusDetail = $"Container {containerName} ativo. Amostra em {GetNow():HH:mm:ss}.",
            RuntimeModeLabel = runtimeMode.Label,
            RuntimeModeDetail = runtimeMode.Detail,
            Cpu = CreateCpuMetric(stats),
            Memory = CreateMemoryMetric(stats),
            Gpu = gpuMetric,
            CapturedAt = GetNow()
        };
    }

    private async Task<RuntimeModeSnapshot> GetRuntimeModeAsync(CancellationToken cancellationToken)
    {
        var now = GetNow();
        if (cachedRuntimeMode is not null && now - cachedRuntimeModeAt < RuntimeModeTtl)
        {
            return cachedRuntimeMode;
        }

        var inspectCommand = await commandRunner.RunAsync(
            "docker",
            ["inspect", "--format", "{{json .Config.Env}}", containerName],
            cancellationToken);

        cachedRuntimeMode = inspectCommand.Succeeded
            ? ParseRuntimeMode(inspectCommand.StandardOutput)
            : RuntimeModeSnapshot.Cpu("Sem dados de ambiente do container; usando CPU como fallback visual.");

        cachedRuntimeModeAt = now;
        return cachedRuntimeMode;
    }

    private async Task<LlamaRuntimeMetric> GetGpuMetricAsync(RuntimeModeSnapshot runtimeMode, CancellationToken cancellationToken)
    {
        if (!runtimeMode.UsesGpu)
        {
            return LlamaRuntimeMetric.Unavailable("Runtime em CPU. Nenhum uso de GPU foi solicitado.");
        }

        if (runtimeMode.Vendor == "nvidia")
        {
            var nvidiaCommand = await commandRunner.RunAsync(
                "nvidia-smi",
                ["--query-gpu=name,utilization.gpu,memory.used,memory.total", "--format=csv,noheader,nounits"],
                cancellationToken);

            if (!nvidiaCommand.Succeeded || string.IsNullOrWhiteSpace(nvidiaCommand.StandardOutput))
            {
                var details = string.IsNullOrWhiteSpace(nvidiaCommand.StandardError)
                    ? "O host nao retornou leitura de GPU via nvidia-smi."
                    : nvidiaCommand.StandardError.Trim();

                return LlamaRuntimeMetric.Unavailable(details);
            }

            var gpu = ParseNvidiaStats(nvidiaCommand.StandardOutput);
            if (gpu is null)
            {
                return LlamaRuntimeMetric.Unavailable("Nao consegui interpretar a saida do nvidia-smi.");
            }

            return new LlamaRuntimeMetric
            {
                ShortValue = $"{FormatPercent(gpu.UtilizationPercent)} | {FormatBytes(gpu.MemoryUsedBytes)}",
                DetailValue = $"{gpu.Name} | {FormatPercent(gpu.UtilizationPercent)} | {FormatBytes(gpu.MemoryUsedBytes)} / {FormatBytes(gpu.MemoryTotalBytes)}",
                Percent = gpu.UtilizationPercent,
                IsAvailable = true
            };
        }

        return LlamaRuntimeMetric.Unavailable(
            runtimeMode.Vendor switch
            {
                "amd" => "Runtime em AMD ROCm. A telemetria de GPU ainda depende de utilitarios do host e nao foi encontrada nesta maquina.",
                "intel" => "Runtime em Intel Vulkan. A telemetria de GPU ainda depende de utilitarios do host e nao foi encontrada nesta maquina.",
                _ => "A GPU esta habilitada, mas nao consegui um coletor compativel com este host."
            });
    }

    private static RuntimeModeSnapshot ParseRuntimeMode(string output)
    {
        try
        {
            var envEntries = JsonSerializer.Deserialize<string[]>(output) ?? [];
            var env = envEntries
                .Select(entry => entry.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

            if (env.TryGetValue("OLLAMA_ACCELERATION_MODE", out var accelerationMode) && !string.IsNullOrWhiteSpace(accelerationMode))
            {
                return NormalizeRuntimeMode(accelerationMode, env);
            }

            if (env.TryGetValue("OLLAMA_GPU_VENDOR", out var gpuVendor) && !string.IsNullOrWhiteSpace(gpuVendor))
            {
                return NormalizeRuntimeMode(gpuVendor, env);
            }

            if (env.TryGetValue("NVIDIA_VISIBLE_DEVICES", out var nvidiaDevices) && !string.IsNullOrWhiteSpace(nvidiaDevices))
            {
                return RuntimeModeSnapshot.Nvidia("Detectado via variaveis NVIDIA do container.");
            }

            if (env.TryGetValue("ROCR_VISIBLE_DEVICES", out var rocrDevices) && !string.IsNullOrWhiteSpace(rocrDevices))
            {
                return RuntimeModeSnapshot.Amd("Detectado via variaveis ROCm do container.");
            }

            if (env.TryGetValue("OLLAMA_VULKAN", out var vulkanEnabled) && vulkanEnabled == "1")
            {
                return RuntimeModeSnapshot.Intel("Detectado via backend Vulkan do container.");
            }
        }
        catch (JsonException)
        {
            // Ignora e cai para CPU.
        }

        return RuntimeModeSnapshot.Cpu("Nenhuma pista de aceleracao foi encontrada no container.");
    }

    private static RuntimeModeSnapshot NormalizeRuntimeMode(string rawMode, IReadOnlyDictionary<string, string> env)
    {
        var normalized = rawMode.Trim().ToLowerInvariant();

        if (normalized.Contains("nvidia") || normalized.Contains("cuda"))
        {
            return RuntimeModeSnapshot.Nvidia("Runtime configurado para NVIDIA CUDA.");
        }

        if (normalized.Contains("amd") || normalized.Contains("rocm"))
        {
            return RuntimeModeSnapshot.Amd("Runtime configurado para AMD ROCm.");
        }

        if (normalized.Contains("intel") || normalized.Contains("vulkan"))
        {
            return RuntimeModeSnapshot.Intel("Runtime configurado para Intel Vulkan.");
        }

        if (normalized.Contains("cpu"))
        {
            return RuntimeModeSnapshot.Cpu("Runtime configurado para CPU.");
        }

        if (env.TryGetValue("NVIDIA_VISIBLE_DEVICES", out var nvidiaDevices) && !string.IsNullOrWhiteSpace(nvidiaDevices))
        {
            return RuntimeModeSnapshot.Nvidia("Detectado via variaveis NVIDIA do container.");
        }

        if (env.TryGetValue("ROCR_VISIBLE_DEVICES", out var rocrDevices) && !string.IsNullOrWhiteSpace(rocrDevices))
        {
            return RuntimeModeSnapshot.Amd("Detectado via variaveis ROCm do container.");
        }

        return RuntimeModeSnapshot.Cpu("Nao reconheci o modo de aceleracao retornado pelo container.");
    }

    private static DockerStatsSnapshot ParseDockerStats(string output)
    {
        var firstJsonLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.TrimStart().StartsWith('{'));

        if (string.IsNullOrWhiteSpace(firstJsonLine))
        {
            return new DockerStatsSnapshot();
        }

        var row = JsonSerializer.Deserialize<DockerStatsRow>(firstJsonLine) ?? new DockerStatsRow();
        var memoryParts = row.MemUsage.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return new DockerStatsSnapshot
        {
            Name = row.Name,
            CpuPercent = ParsePercent(row.CPUPerc),
            MemoryPercent = ParsePercent(row.MemPerc),
            MemoryUsedBytes = memoryParts.Length > 0 ? ParseByteSize(memoryParts[0]) : null,
            MemoryLimitBytes = memoryParts.Length > 1 ? ParseByteSize(memoryParts[1]) : null
        };
    }

    private static NvidaHostSnapshot? ParseNvidiaStats(string output)
    {
        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0)
        {
            return null;
        }

        var entries = new List<NvidaHostSnapshot>();

        foreach (var line in lines)
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            var utilization = ParseNullableDouble(parts[1]);
            var usedMiB = ParseNullableDouble(parts[2]);
            var totalMiB = ParseNullableDouble(parts[3]);

            if (utilization is null || usedMiB is null || totalMiB is null)
            {
                continue;
            }

            entries.Add(new NvidaHostSnapshot(
                parts[0],
                utilization.Value,
                usedMiB.Value * 1024d * 1024d,
                totalMiB.Value * 1024d * 1024d));
        }

        if (entries.Count == 0)
        {
            return null;
        }

        if (entries.Count == 1)
        {
            return entries[0];
        }

        return new NvidaHostSnapshot(
            $"{entries.Count} GPUs NVIDIA",
            entries.Max(entry => entry.UtilizationPercent),
            entries.Sum(entry => entry.MemoryUsedBytes),
            entries.Sum(entry => entry.MemoryTotalBytes));
    }

    private static LlamaRuntimeMetric CreateCpuMetric(DockerStatsSnapshot stats)
    {
        return stats.CpuPercent is null
            ? LlamaRuntimeMetric.Unavailable("O docker stats nao retornou CPU%.")
            : new LlamaRuntimeMetric
            {
                ShortValue = FormatPercent(stats.CpuPercent.Value),
                DetailValue = $"CPU do container {stats.Name}: {FormatPercent(stats.CpuPercent.Value)}",
                Percent = stats.CpuPercent,
                IsAvailable = true
            };
    }

    private static LlamaRuntimeMetric CreateMemoryMetric(DockerStatsSnapshot stats)
    {
        if (stats.MemoryUsedBytes is null)
        {
            return LlamaRuntimeMetric.Unavailable("O docker stats nao retornou uso de memoria.");
        }

        var detail = stats.MemoryLimitBytes is null
            ? $"Memoria do container {stats.Name}: {FormatBytes(stats.MemoryUsedBytes.Value)}"
            : $"Memoria do container {stats.Name}: {FormatBytes(stats.MemoryUsedBytes.Value)} / {FormatBytes(stats.MemoryLimitBytes.Value)} ({FormatPercent(stats.MemoryPercent)})";

        return new LlamaRuntimeMetric
        {
            ShortValue = FormatBytes(stats.MemoryUsedBytes.Value),
            DetailValue = detail,
            Percent = stats.MemoryPercent,
            IsAvailable = true
        };
    }

    private DateTimeOffset GetNow() => timeProvider.GetUtcNow();

    private static double? ParsePercent(string value)
    {
        return ParseNullableDouble(value.Replace("%", string.Empty, StringComparison.Ordinal));
    }

    private static double? ParseNullableDouble(string value)
    {
        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static double? ParseByteSize(string value)
    {
        var match = Regex.Match(value.Trim(), @"^(?<number>[0-9]+(?:\.[0-9]+)?)\s*(?<unit>[A-Za-z]+)$");
        if (!match.Success)
        {
            return null;
        }

        var numericValue = ParseNullableDouble(match.Groups["number"].Value);
        if (numericValue is null)
        {
            return null;
        }

        return match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => numericValue,
            "KB" => numericValue * 1000d,
            "MB" => numericValue * 1000d * 1000d,
            "GB" => numericValue * 1000d * 1000d * 1000d,
            "TB" => numericValue * 1000d * 1000d * 1000d * 1000d,
            "KIB" => numericValue * 1024d,
            "MIB" => numericValue * 1024d * 1024d,
            "GIB" => numericValue * 1024d * 1024d * 1024d,
            "TIB" => numericValue * 1024d * 1024d * 1024d * 1024d,
            _ => null
        };
    }

    private static string FormatBytes(double? bytes)
    {
        if (bytes is null)
        {
            return "--";
        }

        var absolute = Math.Abs(bytes.Value);
        if (absolute >= 1024d * 1024d * 1024d)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes.Value / (1024d * 1024d * 1024d):0.0} GiB");
        }

        if (absolute >= 1024d * 1024d)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes.Value / (1024d * 1024d):0.#} MiB");
        }

        if (absolute >= 1024d)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{bytes.Value / 1024d:0.#} KiB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes.Value:0} B");
    }

    private static string FormatPercent(double? value)
    {
        return value is null
            ? "--"
            : string.Create(CultureInfo.InvariantCulture, $"{value.Value:0.#}%");
    }

    private sealed class ProcessRuntimeCommandRunner : IRuntimeCommandRunner
    {
        public async Task<RuntimeCommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return new RuntimeCommandResult(-1, string.Empty, ex.Message);
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            return new RuntimeCommandResult(
                process.ExitCode,
                await stdout,
                await stderr);
        }
    }

    private sealed class DockerStatsRow
    {
        public string CPUPerc { get; set; } = string.Empty;

        public string MemPerc { get; set; } = string.Empty;

        public string MemUsage { get; set; } = string.Empty;

        public string Name { get; set; } = DefaultContainerName;
    }

    private sealed class DockerStatsSnapshot
    {
        public string Name { get; init; } = DefaultContainerName;

        public double? CpuPercent { get; init; }

        public double? MemoryPercent { get; init; }

        public double? MemoryUsedBytes { get; init; }

        public double? MemoryLimitBytes { get; init; }
    }

    private sealed record RuntimeModeSnapshot(string Label, string Detail, string Vendor, bool UsesGpu)
    {
        public static RuntimeModeSnapshot Nvidia(string detail) => new("NVIDIA CUDA", detail, "nvidia", true);

        public static RuntimeModeSnapshot Amd(string detail) => new("AMD ROCm", detail, "amd", true);

        public static RuntimeModeSnapshot Intel(string detail) => new("Intel Vulkan", detail, "intel", true);

        public static RuntimeModeSnapshot Cpu(string detail) => new("CPU runtime", detail, "cpu", false);
    }

    private sealed record NvidaHostSnapshot(string Name, double UtilizationPercent, double MemoryUsedBytes, double MemoryTotalBytes);
}
