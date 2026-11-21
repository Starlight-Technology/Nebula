namespace Nebula.Core.Telemetry;

public sealed record HostTelemetrySnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Source { get; init; } = "local";

    public HostCpuTelemetry Cpu { get; init; } = new();

    public HostMemoryTelemetry Memory { get; init; } = new();

    public IReadOnlyList<HostGpuTelemetry> Gpus { get; init; } = [];

    public IReadOnlyList<HostDiskTelemetry> Disks { get; init; } = [];

    public OllamaContainerTelemetry? Ollama { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record HostCpuTelemetry
{
    public string Model { get; init; } = string.Empty;

    public int LogicalCores { get; init; }

    public double? UsagePercent { get; init; }

    public double? CgroupLimitCores { get; init; }

    public string Detail { get; init; } = string.Empty;
}

public sealed record HostMemoryTelemetry
{
    public long TotalBytes { get; init; }

    public long AvailableBytes { get; init; }

    public long UsedBytes { get; init; }

    public double? UsagePercent { get; init; }

    public long? CgroupLimitBytes { get; init; }

    public long? CgroupUsedBytes { get; init; }

    public string Detail { get; init; } = string.Empty;
}

public sealed record HostGpuTelemetry
{
    public string Vendor { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string PciId { get; init; } = string.Empty;

    public string Driver { get; init; } = string.Empty;

    public double? UtilizationPercent { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public long? MemoryTotalBytes { get; init; }

    public double? TemperatureCelsius { get; init; }

    public string Source { get; init; } = string.Empty;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(Name);
}

public sealed record HostDiskTelemetry
{
    public string Name { get; init; } = string.Empty;

    public string MountPoint { get; init; } = string.Empty;

    public string FileSystem { get; init; } = string.Empty;

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public long UsedBytes { get; init; }

    public double? UsagePercent { get; init; }

    public bool IsHost { get; init; }
}

public sealed record OllamaContainerTelemetry
{
    public bool IsAvailable { get; init; }

    public string StatusLabel { get; init; } = "Ollama offline";

    public string StatusDetail { get; init; } = "Sem leitura do container.";

    public string ContainerName { get; init; } = "ollama";

    public double? CpuPercent { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public long? MemoryLimitBytes { get; init; }

    public double? MemoryPercent { get; init; }

    public string RuntimeModeLabel { get; init; } = "Modo nao identificado";

    public string RuntimeModeDetail { get; init; } = string.Empty;

    public string Vendor { get; init; } = string.Empty;

    public bool UsesGpu { get; init; }
}
