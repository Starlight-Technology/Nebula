using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Nebula.Core.Telemetry;

namespace Nebula.Llama.Client.Telemetry;

public interface IHostHardwareProbe
{
    HostTelemetrySnapshot Probe();
}

public sealed partial class HostHardwareProbe : IHostHardwareProbe
{
    private readonly string procRoot;
    private readonly string sysRoot;
    private readonly string hostRoot;
    private readonly IRuntimeCommandRunner? commandRunner;
    private readonly TimeProvider timeProvider;
    private readonly object cpuGate = new();

    private CpuTimes? previousCpuTimes;
    private bool nvidiaSmiUnavailable;
    private DateTimeOffset nvidiaSmiRetryAfter;

    public HostHardwareProbe(
        string? procRoot = null,
        string? sysRoot = null,
        string? hostRoot = null,
        IRuntimeCommandRunner? commandRunner = null,
        TimeProvider? timeProvider = null)
    {
        this.procRoot = NormalizeRoot(procRoot ?? "/proc");
        this.sysRoot = NormalizeRoot(sysRoot ?? "/sys");
        this.hostRoot = NormalizeRoot(hostRoot ?? "/");
        this.commandRunner = commandRunner;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public HostTelemetrySnapshot Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return ProbeGeneric();
        }

        var warnings = new List<string>();
        var cpu = ProbeCpu(warnings);
        var memory = ProbeMemory(warnings);
        var gpus = ProbeGpus(warnings);
        var disks = ProbeDisks(warnings);

        return new HostTelemetrySnapshot
        {
            CapturedAt = timeProvider.GetUtcNow(),
            Source = "local",
            Cpu = cpu,
            Memory = memory,
            Gpus = gpus,
            Disks = disks,
            Warnings = warnings
        };
    }

    private HostCpuTelemetry ProbeCpu(List<string> warnings)
    {
        var model = string.Empty;
        var logicalCores = 0;

        var cpuInfo = ReadText(Path.Combine(procRoot, "cpuinfo"));
        if (!string.IsNullOrWhiteSpace(cpuInfo))
        {
            foreach (var line in cpuInfo.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();

                if (key.Equals("model name", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(model))
                {
                    model = value;
                }
                else if (key.Equals("processor", StringComparison.OrdinalIgnoreCase))
                {
                    logicalCores++;
                }
            }
        }

        if (logicalCores == 0)
        {
            logicalCores = Environment.ProcessorCount;
        }

        var usage = ProbeCpuUsage();
        var cgroupLimit = ProbeCpuCgroupLimit();

        if (string.IsNullOrWhiteSpace(model))
        {
            model = RuntimeInformation.ProcessArchitecture.ToString();
            warnings.Add("Nao consegui ler o modelo da CPU em /proc/cpuinfo.");
        }

        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(model))
        {
            detailParts.Add(model);
        }

        detailParts.Add($"{logicalCores} threads");
        if (cgroupLimit is not null)
        {
            detailParts.Add($"limite cgroup {cgroupLimit.Value:0.##} CPU(s)");
        }

        return new HostCpuTelemetry
        {
            Model = model,
            LogicalCores = logicalCores,
            UsagePercent = usage,
            CgroupLimitCores = cgroupLimit,
            Detail = string.Join(" | ", detailParts)
        };
    }

    private HostMemoryTelemetry ProbeMemory(List<string> warnings)
    {
        long total = 0;
        long available = 0;

        var memInfo = ReadText(Path.Combine(procRoot, "meminfo"));
        if (!string.IsNullOrWhiteSpace(memInfo))
        {
            var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in memInfo.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                var key = line[..separator].Trim();
                var valueText = line[(separator + 1)..].Trim();
                var value = ParseFirstLong(valueText);
                if (value is not null)
                {
                    values[key] = value.Value * 1024L;
                }
            }

            values.TryGetValue("MemTotal", out total);
            if (!values.TryGetValue("MemAvailable", out available))
            {
                values.TryGetValue("MemFree", out available);
            }
        }

        if (total <= 0)
        {
            var gcInfo = GC.GetGCMemoryInfo();
            total = gcInfo.TotalAvailableMemoryBytes;
            available = gcInfo.TotalAvailableMemoryBytes;
            warnings.Add("Nao consegui ler /proc/meminfo; usando a memoria disponivel para o processo.");
        }

        var used = Math.Max(0, total - available);
        var percent = total > 0 ? (double?)(used * 100d / total) : null;

        var cgroupLimit = ProbeCgroupLong("memory.max");
        var cgroupUsed = ProbeCgroupLong("memory.current");

        return new HostMemoryTelemetry
        {
            TotalBytes = total,
            AvailableBytes = available,
            UsedBytes = used,
            UsagePercent = percent,
            CgroupLimitBytes = cgroupLimit,
            CgroupUsedBytes = cgroupUsed,
            Detail = $"{FormatBytes(used)} / {FormatBytes(total)} ({FormatPercent(percent)})"
        };
    }

    private IReadOnlyList<HostGpuTelemetry> ProbeGpus(List<string> warnings)
    {
        var gpus = new Dictionary<string, GpuBuilder>(StringComparer.OrdinalIgnoreCase);

        ProbeNvidiaProc(gpus);
        ProbeDrmSysfs(gpus, warnings);
        ProbeNvidiaSmi(gpus, warnings);
        ProbeAmdSysfs(gpus, warnings);

        return gpus.Values
            .OrderBy(gpu => VendorPriority(gpu.Vendor))
            .ThenBy(gpu => gpu.Name, StringComparer.OrdinalIgnoreCase)
            .Select(gpu => gpu.ToSnapshot())
            .ToList();
    }

    private void ProbeNvidiaProc(Dictionary<string, GpuBuilder> gpus)
    {
        var root = Path.Combine(procRoot, "driver", "nvidia", "gpus");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in SafeEnumerateDirectories(root))
        {
            var information = ReadText(Path.Combine(directory, "information"));
            if (string.IsNullOrWhiteSpace(information))
            {
                continue;
            }

            var model = ReadField(information, "Model");
            var busLocation = ReadField(information, "Bus Location");

            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            var gpu = GetOrAdd(gpus, busLocation, "nvidia");
            gpu.Name = model;
            gpu.PciBusId = string.IsNullOrWhiteSpace(busLocation) ? gpu.PciBusId : busLocation;
            gpu.PciId = string.IsNullOrWhiteSpace(gpu.PciId) ? busLocation : gpu.PciId;
            gpu.Source = "proc";
        }
    }

    private void ProbeDrmSysfs(Dictionary<string, GpuBuilder> gpus, List<string> warnings)
    {
        var root = Path.Combine(sysRoot, "class", "drm");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in SafeEnumerateDirectories(root))
        {
            var cardName = Path.GetFileName(directory);
            if (!CardDirectoryRegex().IsMatch(cardName))
            {
                continue;
            }

            var device = Path.Combine(directory, "device");
            if (!Directory.Exists(device))
            {
                continue;
            }

            var vendorId = ReadText(Path.Combine(device, "vendor")).Trim();
            if (string.IsNullOrWhiteSpace(vendorId))
            {
                continue;
            }

            var vendor = NormalizeVendor(vendorId);
            var uevent = ReadText(Path.Combine(device, "uevent"));
            var pciId = ReadField(uevent, "PCI_ID");
            var pciBusId = ReadField(uevent, "PCI_SLOT_NAME");
            var driver = ReadField(uevent, "DRIVER");
            var label = ReadText(Path.Combine(device, "label")).Trim();

            var key = string.IsNullOrWhiteSpace(pciBusId) ? pciId : pciBusId;
            var gpu = GetOrAdd(gpus, key, vendor);
            gpu.PciBusId = string.IsNullOrWhiteSpace(pciBusId) ? gpu.PciBusId : pciBusId;
            gpu.DevicePath = device;
            gpu.Vendor = vendor;
            gpu.Driver = string.IsNullOrWhiteSpace(driver) ? gpu.Driver : driver;
            gpu.PciId = string.IsNullOrWhiteSpace(pciId) ? gpu.PciId : pciId;
            gpu.Source = string.IsNullOrWhiteSpace(gpu.Source) ? "sysfs" : gpu.Source;

            if (string.IsNullOrWhiteSpace(gpu.Name))
            {
                gpu.Name = !string.IsNullOrWhiteSpace(label)
                    ? label
                    : VendorDisplayName(vendor);
            }

            if (string.IsNullOrWhiteSpace(gpu.Name))
            {
                warnings.Add($"GPU {cardName} detectada, mas sem nome legivel.");
                gpu.Name = $"GPU {cardName}";
            }
        }
    }

    private void ProbeNvidiaSmi(Dictionary<string, GpuBuilder> gpus, List<string> warnings)
    {
        if (commandRunner is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        if (nvidiaSmiUnavailable && now < nvidiaSmiRetryAfter)
        {
            return;
        }

        MarkNvidiaSmiUnavailable(now, warnings, "sonda nvidia-smi desativada temporariamente ate estabilizar o subprocesso.");
    }

    private void MarkNvidiaSmiUnavailable(DateTimeOffset now, List<string> warnings, string? reason)
    {
        nvidiaSmiUnavailable = true;
        nvidiaSmiRetryAfter = now.AddMinutes(5);

        var detail = string.IsNullOrWhiteSpace(reason)
            ? "comando nao encontrado"
            : reason.Trim();
        warnings.Add($"nvidia-smi indisponivel: {detail}");
    }

    private void ProbeAmdSysfs(Dictionary<string, GpuBuilder> gpus, List<string> warnings)
    {
        foreach (var gpu in gpus.Values.Where(candidate =>
                     candidate.Vendor.Equals("amd", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(gpu.DevicePath))
            {
                continue;
            }

            var busy = ParseNullableDouble(ReadText(Path.Combine(gpu.DevicePath, "gpu_busy_percent")));
            var used = ParseNullableLong(ReadText(Path.Combine(gpu.DevicePath, "mem_info_vram_used")));
            var total = ParseNullableLong(ReadText(Path.Combine(gpu.DevicePath, "mem_info_vram_total")));

            gpu.UtilizationPercent = busy;
            gpu.MemoryUsedBytes = used;
            gpu.MemoryTotalBytes = total;

            var hwmonRoot = Path.Combine(gpu.DevicePath, "hwmon");
            if (Directory.Exists(hwmonRoot))
            {
                foreach (var hwmon in SafeEnumerateDirectories(hwmonRoot))
                {
                    var temperature = ParseNullableDouble(ReadText(Path.Combine(hwmon, "temp1_input")));
                    if (temperature is not null)
                    {
                        gpu.TemperatureCelsius = temperature.Value / 1000d;
                        break;
                    }
                }
            }

            if (busy is not null || used is not null)
            {
                gpu.Source = "sysfs";
            }
            else
            {
                warnings.Add($"GPU AMD {gpu.Name} detectada sem metricas de uso em sysfs.");
            }
        }
    }

    private IReadOnlyList<HostDiskTelemetry> ProbeDisks(List<string> warnings)
    {
        var disks = new List<HostDiskTelemetry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddDisk(disks, seen, hostRoot, "Host", isHost: true, warnings);

        var dataRoot = Path.Combine(hostRoot, "var");
        if (!dataRoot.Equals(hostRoot, StringComparison.OrdinalIgnoreCase))
        {
            AddDisk(disks, seen, dataRoot, "Host data", isHost: true, warnings);
        }

        var tempRoot = Path.Combine(hostRoot, "tmp");
        if (!tempRoot.Equals(hostRoot, StringComparison.OrdinalIgnoreCase))
        {
            AddDisk(disks, seen, tempRoot, "Temp", isHost: false, warnings);
        }

        return disks
            .OrderByDescending(disk => disk.TotalBytes)
            .ThenByDescending(disk => disk.IsHost)
            .ToList();
    }

    private static void AddDisk(
        List<HostDiskTelemetry> disks,
        HashSet<string> seen,
        string path,
        string label,
        bool isHost,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            var drive = new DriveInfo(path);
            if (drive.TotalSize <= 0)
            {
                return;
            }

            var key = $"{drive.Name}|{drive.TotalSize}";
            if (!seen.Add(key))
            {
                return;
            }

            var free = Math.Max(0, drive.AvailableFreeSpace);
            var used = Math.Max(0, drive.TotalSize - free);

            disks.Add(new HostDiskTelemetry
            {
                Name = label,
                MountPoint = path,
                FileSystem = drive.DriveFormat,
                TotalBytes = drive.TotalSize,
                FreeBytes = free,
                UsedBytes = used,
                UsagePercent = drive.TotalSize > 0 ? used * 100d / drive.TotalSize : null,
                IsHost = isHost
            });
        }
        catch (Exception ex)
        {
            warnings.Add($"Nao consegui ler o disco {path}: {ex.Message}");
        }
    }

    private HostTelemetrySnapshot ProbeGeneric()
    {
        var memory = GC.GetGCMemoryInfo();
        var disks = new List<HostDiskTelemetry>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                var free = Math.Max(0, drive.AvailableFreeSpace);
                var used = Math.Max(0, drive.TotalSize - free);

                disks.Add(new HostDiskTelemetry
                {
                    Name = drive.Name,
                    MountPoint = drive.RootDirectory.FullName,
                    FileSystem = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = free,
                    UsedBytes = used,
                    UsagePercent = used * 100d / drive.TotalSize,
                    IsHost = true
                });
            }
            catch
            {
                // Ignora unidades que nao podem ser lidas.
            }
        }

        return new HostTelemetrySnapshot
        {
            CapturedAt = timeProvider.GetUtcNow(),
            Source = "local",
            Cpu = new HostCpuTelemetry
            {
                Model = RuntimeInformation.OSDescription,
                LogicalCores = Environment.ProcessorCount,
                Detail = $"{Environment.ProcessorCount} threads"
            },
            Memory = new HostMemoryTelemetry
            {
                TotalBytes = memory.TotalAvailableMemoryBytes,
                AvailableBytes = memory.TotalAvailableMemoryBytes,
                UsedBytes = 0,
                Detail = "Memoria total indisponivel fora do Linux."
            },
            Disks = disks,
            Warnings = ["Leitura detalhada de hardware esta disponivel apenas no Linux."]
        };
    }

    private double? ProbeCpuUsage()
    {
        var stat = ReadText(Path.Combine(procRoot, "stat"));
        if (string.IsNullOrWhiteSpace(stat))
        {
            return null;
        }

        var cpuLine = stat
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(cpuLine))
        {
            return null;
        }

        var parts = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
        {
            return null;
        }

        var values = new long[parts.Length - 1];
        for (var index = 1; index < parts.Length; index++)
        {
            if (!long.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[index - 1]))
            {
                return null;
            }
        }

        var total = values.Sum();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        var current = new CpuTimes(total, idle);

        lock (cpuGate)
        {
            var previous = previousCpuTimes;
            previousCpuTimes = current;

            if (previous is null || total <= previous.Total || idle < previous.Idle)
            {
                return null;
            }

            var totalDelta = total - previous.Total;
            var idleDelta = idle - previous.Idle;
            if (totalDelta <= 0)
            {
                return null;
            }

            return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0d, 100d);
        }
    }

    private double? ProbeCpuCgroupLimit()
    {
        var value = ReadText(Path.Combine(sysRoot, "fs", "cgroup", "cpu.max")).Trim();
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("max", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var quota) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var period) ||
            period <= 0)
        {
            return null;
        }

        return quota / period;
    }

    private long? ProbeCgroupLong(string fileName)
    {
        var value = ReadText(Path.Combine(sysRoot, "fs", "cgroup", fileName)).Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static GpuBuilder GetOrAdd(
        Dictionary<string, GpuBuilder> gpus,
        string? key,
        string vendor)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(key)
            ? $"vendor:{vendor}:{Guid.NewGuid():N}"
            : key.Trim().ToUpperInvariant();

        if (!gpus.TryGetValue(normalizedKey, out var gpu))
        {
            gpu = new GpuBuilder { Vendor = vendor };
            gpus[normalizedKey] = gpu;
        }

        return gpu;
    }

    private static string NormalizeVendor(string vendorId)
    {
        return vendorId.Trim().ToUpperInvariant() switch
        {
            "0X10DE" => "nvidia",
            "0X1002" => "amd",
            "0X8086" => "intel",
            _ => "unknown"
        };
    }

    private static string VendorDisplayName(string vendor)
    {
        return vendor switch
        {
            "nvidia" => "NVIDIA GPU",
            "amd" => "AMD GPU",
            "intel" => "Intel GPU",
            _ => "GPU"
        };
    }

    private static int VendorPriority(string vendor)
    {
        return vendor.ToLowerInvariant() switch
        {
            "nvidia" => 0,
            "amd" => 1,
            "intel" => 2,
            _ => 3
        };
    }

    private static string ReadField(string text, string key)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = line.IndexOf('=');
            if (equals > 0 &&
                line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(equals + 1)..].Trim();
            }

            var separator = line.IndexOf(':');
            if (separator >= 0 &&
                line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    private static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static long? ParseFirstLong(string text)
    {
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static long? ParseNullableLong(string text)
    {
        return long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? ParseNullableDouble(string text)
    {
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string NormalizeRoot(string path)
    {
        var trimmed = path.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "/";
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private static string FormatBytes(long bytes)
    {
        var absolute = Math.Abs((double)bytes);
        if (absolute >= 1024d * 1024d * 1024d)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d * 1024d):0.0} GiB");
        }

        if (absolute >= 1024d * 1024d)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):0.#} MiB");
        }

        if (absolute >= 1024d)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.#} KiB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes:0} B");
    }

    private static string FormatPercent(double? value)
    {
        return value is null
            ? "--"
            : string.Create(CultureInfo.InvariantCulture, $"{value.Value:0.#}%");
    }

    [GeneratedRegex(@"^card\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex CardDirectoryRegex();

    private sealed class GpuBuilder
    {
        public string Vendor { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string PciId { get; set; } = string.Empty;

        public string PciBusId { get; set; } = string.Empty;

        public string Driver { get; set; } = string.Empty;

        public string DevicePath { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public double? UtilizationPercent { get; set; }

        public long? MemoryUsedBytes { get; set; }

        public long? MemoryTotalBytes { get; set; }

        public double? TemperatureCelsius { get; set; }

        public HostGpuTelemetry ToSnapshot()
        {
            return new HostGpuTelemetry
            {
                Vendor = Vendor,
                Name = Name,
                PciId = PciId,
                Driver = Driver,
                UtilizationPercent = UtilizationPercent,
                MemoryUsedBytes = MemoryUsedBytes,
                MemoryTotalBytes = MemoryTotalBytes,
                TemperatureCelsius = TemperatureCelsius,
                Source = Source
            };
        }
    }

    private sealed record CpuTimes(long Total, long Idle);
}
