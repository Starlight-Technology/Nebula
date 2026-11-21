using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Nebula.Core.Telemetry;
using Nebula.Llama.Client.Telemetry;

namespace Nebula.Llama.Client;

public sealed class LlamaRuntimeTelemetryService : ILlamaRuntimeTelemetryService
{
    private const string DefaultContainerName = "ollama";
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RuntimeModeTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OllamaStatusTtl = TimeSpan.FromSeconds(5);

    private readonly IRuntimeCommandRunner commandRunner;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string containerName;
    private readonly TimeProvider timeProvider;
    private readonly IHostHardwareProbe hardwareProbe;
    private readonly ITelemetrySource? telemetrySource;
    private readonly ILlamaClient? llamaClient;

    private LlamaRuntimeTelemetrySnapshot? cachedSnapshot;
    private DateTimeOffset cachedSnapshotAt;
    private RuntimeModeSnapshot? cachedRuntimeMode;
    private DateTimeOffset cachedRuntimeModeAt;
    private OllamaStatusSnapshot? cachedOllamaStatus;
    private DateTimeOffset cachedOllamaStatusAt;

    public LlamaRuntimeTelemetryService(
        IRuntimeCommandRunner? commandRunner = null,
        string? containerName = null,
        TimeProvider? timeProvider = null,
        IHostHardwareProbe? hardwareProbe = null,
        ITelemetrySource? telemetrySource = null,
        ILlamaClient? llamaClient = null)
    {
        this.commandRunner = commandRunner ?? new ProcessRuntimeCommandRunner();
        this.containerName = string.IsNullOrWhiteSpace(containerName)
            ? Environment.GetEnvironmentVariable("OLLAMA_CONTAINER_NAME") ?? DefaultContainerName
            : containerName.Trim();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.hardwareProbe = hardwareProbe ?? new HostHardwareProbe(
            commandRunner: this.commandRunner,
            timeProvider: this.timeProvider);
        this.telemetrySource = telemetrySource;
        this.llamaClient = llamaClient;
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
        var hardware = await GetHardwareSnapshotAsync(cancellationToken);
        var (dockerStats, dockerError) = await TryGetDockerStatsAsync(cancellationToken);
        var runtimeMode = dockerStats is null
            ? null
            : await GetRuntimeModeAsync(cancellationToken);
        var ollamaStatus = await GetOllamaStatusAsync(cancellationToken);

        var externalOllama = hardware.Ollama;
        var isOnline = externalOllama?.IsAvailable == true ||
                       dockerStats is not null ||
                       ollamaStatus?.IsOnline == true;

        var statusLabel = externalOllama?.StatusLabel;
        if (string.IsNullOrWhiteSpace(statusLabel))
        {
            statusLabel = isOnline ? "Ollama online" : "Ollama offline";
        }

        var statusDetail = BuildStatusDetail(
            externalOllama,
            dockerStats,
            dockerError,
            ollamaStatus,
            hardware);

        var cpu = CreateCpuMetric(externalOllama, dockerStats, hardware.Cpu);
        var memory = CreateMemoryMetric(externalOllama, dockerStats, hardware.Memory);
        var gpu = CreateGpuMetric(hardware.Gpus) ??
                  (runtimeMode?.UsesGpu == true
                      ? await GetGpuMetricAsync(runtimeMode, cancellationToken)
                      : runtimeMode is not null
                          ? LlamaRuntimeMetric.Unavailable("Runtime em CPU. Nenhuma GPU foi solicitada.")
                          : LlamaRuntimeMetric.Unavailable("Nenhuma GPU detectada no host."));
        var disk = CreateDiskMetric(hardware.Disks);
        var mode = ResolveRuntimeMode(externalOllama, runtimeMode, hardware);

        return new LlamaRuntimeTelemetrySnapshot
        {
            IsAvailable = isOnline,
            StatusLabel = statusLabel,
            StatusDetail = statusDetail,
            RuntimeModeLabel = mode.Label,
            RuntimeModeDetail = mode.Detail,
            Cpu = cpu,
            Memory = memory,
            Gpu = gpu,
            Disk = disk,
            Gpus = hardware.Gpus,
            Hardware = hardware,
            MetricsSource = ResolveMetricsSource(externalOllama, dockerStats, hardware),
            ContainerName = externalOllama?.ContainerName ??
                            (string.IsNullOrWhiteSpace(dockerStats?.Name) ? containerName : dockerStats.Name),
            CapturedAt = GetNow(),
            LastError = isOnline ? null : statusDetail
        };
    }

    private async Task<HostTelemetrySnapshot> GetHardwareSnapshotAsync(CancellationToken cancellationToken)
    {
        if (telemetrySource is not null)
        {
            try
            {
                var external = await telemetrySource.GetSnapshotAsync(cancellationToken);
                if (external is not null)
                {
                    return external;
                }
            }
            catch
            {
                // Sidecar indisponivel: cai para a leitura local.
            }
        }

        try
        {
            return hardwareProbe.Probe();
        }
        catch (Exception ex)
        {
            return new HostTelemetrySnapshot
            {
                CapturedAt = GetNow(),
                Source = "local",
                Warnings = [$"Falha ao ler hardware local: {ex.Message}"]
            };
        }
    }

    private async Task<(DockerStatsSnapshot? Stats, string? Error)> TryGetDockerStatsAsync(
        CancellationToken cancellationToken)
    {
        try
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

                return (null, string.IsNullOrWhiteSpace(details)
                    ? $"Nao consegui ler o container {containerName} via docker stats."
                    : details);
            }

            return (ParseDockerStats(statsCommand.StandardOutput), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private async Task<RuntimeModeSnapshot?> GetRuntimeModeAsync(CancellationToken cancellationToken)
    {
        var now = GetNow();
        if (cachedRuntimeMode is not null && now - cachedRuntimeModeAt < RuntimeModeTtl)
        {
            return cachedRuntimeMode;
        }

        try
        {
            var inspectCommand = await commandRunner.RunAsync(
                "docker",
                ["inspect", "--format", "{{json .Config.Env}}", containerName],
                cancellationToken);

            if (!inspectCommand.Succeeded || string.IsNullOrWhiteSpace(inspectCommand.StandardOutput))
            {
                return null;
            }

            cachedRuntimeMode = ParseRuntimeMode(inspectCommand.StandardOutput);
            cachedRuntimeModeAt = now;
            return cachedRuntimeMode;
        }
        catch
        {
            return null;
        }
    }

    private async Task<OllamaStatusSnapshot?> GetOllamaStatusAsync(CancellationToken cancellationToken)
    {
        if (llamaClient is null)
        {
            return null;
        }

        var now = GetNow();
        if (cachedOllamaStatus is not null && now - cachedOllamaStatusAt < OllamaStatusTtl)
        {
            return cachedOllamaStatus;
        }

        string? version = null;
        try
        {
            version = await llamaClient.GetServerVersionAsync(cancellationToken);
        }
        catch
        {
            // O status offline e tratado pelo valor nulo.
        }

        cachedOllamaStatus = new OllamaStatusSnapshot(version is not null, version);
        cachedOllamaStatusAt = now;
        return cachedOllamaStatus;
    }

    private async Task<LlamaRuntimeMetric> GetGpuMetricAsync(
        RuntimeModeSnapshot runtimeMode,
        CancellationToken cancellationToken)
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

    private static LlamaRuntimeMetric CreateCpuMetric(
        OllamaContainerTelemetry? externalOllama,
        DockerStatsSnapshot? dockerStats,
        HostCpuTelemetry cpu)
    {
        if (externalOllama?.CpuPercent is not null)
        {
            return new LlamaRuntimeMetric
            {
                ShortValue = FormatPercent(externalOllama.CpuPercent),
                DetailValue = $"CPU do container {externalOllama.ContainerName}: {FormatPercent(externalOllama.CpuPercent)}",
                Percent = externalOllama.CpuPercent,
                IsAvailable = true
            };
        }

        if (dockerStats?.CpuPercent is not null)
        {
            return new LlamaRuntimeMetric
            {
                ShortValue = FormatPercent(dockerStats.CpuPercent),
                DetailValue = $"CPU do container {dockerStats.Name}: {FormatPercent(dockerStats.CpuPercent)}",
                Percent = dockerStats.CpuPercent,
                IsAvailable = true
            };
        }

        if (string.IsNullOrWhiteSpace(cpu.Model) && cpu.LogicalCores == 0)
        {
            return LlamaRuntimeMetric.Unavailable("Sem leitura de CPU do host.");
        }

        return new LlamaRuntimeMetric
        {
            ShortValue = FormatPercent(cpu.UsagePercent),
            DetailValue = $"CPU do host: {cpu.Detail} | uso {FormatPercent(cpu.UsagePercent)}",
            Percent = cpu.UsagePercent,
            IsAvailable = true
        };
    }

    private static LlamaRuntimeMetric CreateMemoryMetric(
        OllamaContainerTelemetry? externalOllama,
        DockerStatsSnapshot? dockerStats,
        HostMemoryTelemetry memory)
    {
        if (externalOllama?.MemoryUsedBytes is not null)
        {
            return new LlamaRuntimeMetric
            {
                ShortValue = FormatBytes(externalOllama.MemoryUsedBytes),
                DetailValue = $"Memoria do container {externalOllama.ContainerName}: {FormatBytes(externalOllama.MemoryUsedBytes)}" +
                              (externalOllama.MemoryLimitBytes is null
                                  ? string.Empty
                                  : $" / {FormatBytes(externalOllama.MemoryLimitBytes)} ({FormatPercent(externalOllama.MemoryPercent)})"),
                Percent = externalOllama.MemoryPercent,
                IsAvailable = true
            };
        }

        if (dockerStats?.MemoryUsedBytes is not null)
        {
            return new LlamaRuntimeMetric
            {
                ShortValue = FormatBytes(dockerStats.MemoryUsedBytes),
                DetailValue = dockerStats.MemoryLimitBytes is null
                    ? $"Memoria do container {dockerStats.Name}: {FormatBytes(dockerStats.MemoryUsedBytes)}"
                    : $"Memoria do container {dockerStats.Name}: {FormatBytes(dockerStats.MemoryUsedBytes)} / {FormatBytes(dockerStats.MemoryLimitBytes)} ({FormatPercent(dockerStats.MemoryPercent)})",
                Percent = dockerStats.MemoryPercent,
                IsAvailable = true
            };
        }

        if (memory.TotalBytes <= 0)
        {
            return LlamaRuntimeMetric.Unavailable("Sem leitura de memoria do host.");
        }

        return new LlamaRuntimeMetric
        {
            ShortValue = FormatBytes(memory.UsedBytes),
            DetailValue = $"RAM do host: {memory.Detail}",
            Percent = memory.UsagePercent,
            IsAvailable = true
        };
    }

    private static LlamaRuntimeMetric? CreateGpuMetric(IReadOnlyList<HostGpuTelemetry> gpus)
    {
        var gpu = gpus.FirstOrDefault();
        if (gpu is null || string.IsNullOrWhiteSpace(gpu.Name))
        {
            return null;
        }

        var hasUsage = gpu.UtilizationPercent is not null ||
                       gpu.MemoryUsedBytes is not null ||
                       gpu.MemoryTotalBytes is not null;

        if (!hasUsage)
        {
            return new LlamaRuntimeMetric
            {
                ShortValue = "detectada",
                DetailValue = $"{gpu.Name} ({gpu.Vendor}) | uso e VRAM indisponiveis sem coletor da GPU.",
                Percent = null,
                IsAvailable = true
            };
        }

        var memory = gpu.MemoryUsedBytes is null && gpu.MemoryTotalBytes is null
            ? string.Empty
            : $" | {FormatBytes(gpu.MemoryUsedBytes)}" +
              (gpu.MemoryTotalBytes is null ? string.Empty : $" / {FormatBytes(gpu.MemoryTotalBytes)}");

        return new LlamaRuntimeMetric
        {
            ShortValue = $"{FormatPercent(gpu.UtilizationPercent)} | {FormatBytes(gpu.MemoryUsedBytes)}",
            DetailValue = $"{gpu.Name} | {FormatPercent(gpu.UtilizationPercent)}{memory}",
            Percent = gpu.UtilizationPercent,
            IsAvailable = true
        };
    }

    private static LlamaRuntimeMetric CreateDiskMetric(IReadOnlyList<HostDiskTelemetry> disks)
    {
        var disk = disks.FirstOrDefault();
        if (disk is null)
        {
            return LlamaRuntimeMetric.Unavailable("Sem leitura de disco do host.");
        }

        return new LlamaRuntimeMetric
        {
            ShortValue = FormatPercent(disk.UsagePercent),
            DetailValue = $"{disk.MountPoint}: {FormatBytes(disk.UsedBytes)} / {FormatBytes(disk.TotalBytes)} ({FormatPercent(disk.UsagePercent)}) | {FormatBytes(disk.FreeBytes)} livres",
            Percent = disk.UsagePercent,
            IsAvailable = true
        };
    }

    private static RuntimeModeSnapshot ResolveRuntimeMode(
        OllamaContainerTelemetry? externalOllama,
        RuntimeModeSnapshot? dockerMode,
        HostTelemetrySnapshot hardware)
    {
        if (externalOllama is not null && !string.IsNullOrWhiteSpace(externalOllama.RuntimeModeLabel))
        {
            return new RuntimeModeSnapshot(
                externalOllama.RuntimeModeLabel,
                externalOllama.RuntimeModeDetail,
                externalOllama.Vendor,
                externalOllama.UsesGpu);
        }

        if (dockerMode is not null)
        {
            return dockerMode;
        }

        var gpu = hardware.Gpus.FirstOrDefault();
        if (gpu is not null && !string.IsNullOrWhiteSpace(gpu.Name))
        {
            return new RuntimeModeSnapshot(
                "GPU disponivel",
                $"{gpu.Name} detectada no host; modo do Ollama nao confirmado sem acesso ao container.",
                gpu.Vendor,
                false);
        }

        return RuntimeModeSnapshot.Cpu("Nenhuma GPU detectada no host.");
    }

    private static string ResolveMetricsSource(
        OllamaContainerTelemetry? externalOllama,
        DockerStatsSnapshot? dockerStats,
        HostTelemetrySnapshot hardware)
    {
        if (externalOllama is not null)
        {
            return "sidecar+container";
        }

        if (dockerStats is not null)
        {
            return "container";
        }

        return hardware.Source;
    }

    private static string BuildStatusDetail(
        OllamaContainerTelemetry? externalOllama,
        DockerStatsSnapshot? dockerStats,
        string? dockerError,
        OllamaStatusSnapshot? ollamaStatus,
        HostTelemetrySnapshot hardware)
    {
        if (externalOllama is not null)
        {
            return externalOllama.StatusDetail;
        }

        if (dockerStats is not null)
        {
            return $"Container {dockerStats.Name} ativo. Amostra em {DateTimeOffset.UtcNow:HH:mm:ss}.";
        }

        if (ollamaStatus?.IsOnline == true)
        {
            var detail = "Ollama respondeu na API HTTP.";
            if (!string.IsNullOrWhiteSpace(dockerError))
            {
                detail += $" Metricas do container indisponiveis ({dockerError}); exibindo hardware do host.";
            }
            else if (hardware.Source.Equals("sidecar", StringComparison.OrdinalIgnoreCase))
            {
                detail += " Hardware coletado pelo sidecar.";
            }

            return detail;
        }

        return string.IsNullOrWhiteSpace(dockerError)
            ? "Nao consegui falar com o Ollama nem ler o container."
            : $"Ollama offline: {dockerError}";
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

    private static string FormatBytes(long? bytes)
    {
        return FormatBytes(bytes is null ? null : (double?)bytes.Value);
    }

    private static string FormatPercent(double? value)
    {
        return value is null
            ? "--"
            : string.Create(CultureInfo.InvariantCulture, $"{value.Value:0.#}%");
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

    private sealed record OllamaStatusSnapshot(bool IsOnline, string? Version);
}
