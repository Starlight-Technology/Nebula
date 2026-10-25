using Nebula.Llama.Client.Telemetry;

namespace Nebula.Agent.Test.Telemetry;

public sealed class HostHardwareProbeTest : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"nebula-hardware-{Guid.NewGuid():N}");

    [Fact]
    public void probe_must_read_cpu_memory_and_merge_nvidia_proc_with_sysfs()
    {
        Write("proc/cpuinfo", """
            processor : 0
            model name : Test CPU 12700H

            processor : 1
            model name : Test CPU 12700H
            """);
        Write("proc/meminfo", """
            MemTotal:       16777216 kB
            MemAvailable:    8388608 kB
            """);
        Write("proc/stat", "cpu  100 0 100 800 0 0 0 0 0 0");
        Write("proc/driver/nvidia/gpus/0000:01:00.0/information", """
            Model: NVIDIA GeForce RTX 3050 Laptop GPU
            Bus Location: 0000:01:00.0
            """);

        Write("sys/class/drm/card0/device/vendor", "0x10de");
        Write("sys/class/drm/card0/device/uevent", """
            PCI_ID=10DE:25A2
            PCI_SLOT_NAME=0000:01:00.0
            DRIVER=nvidia
            """);
        Write("sys/class/drm/card0/device/label", "NVIDIA GPU");

        Write("sys/class/drm/card1/device/vendor", "0x8086");
        Write("sys/class/drm/card1/device/uevent", """
            PCI_ID=8086:46A6
            PCI_SLOT_NAME=0000:00:02.0
            DRIVER=i915
            """);
        Write("sys/class/drm/card1/device/label", "Onboard - Video");

        Write("sys/fs/cgroup/cpu.max", "max 100000");
        Write("sys/fs/cgroup/memory.max", "max");

        var probe = new HostHardwareProbe(
            procRoot: Path.Combine(root, "proc"),
            sysRoot: Path.Combine(root, "sys"),
            hostRoot: root);

        var first = probe.Probe();

        Assert.Equal("Test CPU 12700H", first.Cpu.Model);
        Assert.Equal(2, first.Cpu.LogicalCores);
        Assert.Equal(16L * 1024 * 1024 * 1024, first.Memory.TotalBytes);
        Assert.Equal(8L * 1024 * 1024 * 1024, first.Memory.AvailableBytes);
        Assert.Null(first.Cpu.UsagePercent);
        Assert.Equal(2, first.Gpus.Count);

        var nvidia = Assert.Single(first.Gpus, gpu => gpu.Vendor == "nvidia");
        Assert.Contains("RTX 3050", nvidia.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(first.Gpus, gpu =>
            gpu.Vendor == "intel" && gpu.Name == "Onboard - Video");

        Write("proc/stat", "cpu  200 0 200 1600 0 0 0 0 0 0");

        var second = probe.Probe();

        Assert.Equal(20, second.Cpu.UsagePercent);
        Assert.Equal(2, second.Gpus.Count);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best effort cleanup for a temporary test directory.
        }
    }
}
