using Nebula.App.Shared.Setup;
using Nebula.Core.Telemetry;
using Nebula.Llama.Client.Telemetry;

namespace Nebula.App.Test;

public class RuntimeSetupAdvisorHardwareTest
{
    [Fact]
    public void build_recommendation_must_use_host_gpu_when_browser_probe_is_empty()
    {
        var hardware = new HostTelemetrySnapshot
        {
            Source = "sidecar",
            Gpus =
            [
                new HostGpuTelemetry
                {
                    Vendor = "nvidia",
                    Name = "NVIDIA GeForce RTX 3050 Laptop GPU",
                    UtilizationPercent = 35,
                    MemoryUsedBytes = 2048L * 1024 * 1024,
                    MemoryTotalBytes = 4096L * 1024 * 1024
                }
            ],
            Disks =
            [
                new HostDiskTelemetry
                {
                    Name = "Host",
                    MountPoint = "/host/root",
                    TotalBytes = 952L * 1024 * 1024 * 1024,
                    FreeBytes = 215L * 1024 * 1024 * 1024,
                    UsedBytes = 731L * 1024 * 1024 * 1024,
                    UsagePercent = 76.8,
                    IsHost = true
                }
            ]
        };

        var advisor = new RuntimeSetupAdvisor(
            "Test shell",
            new FakeHardwareProbe(hardware));

        var recommendation = advisor.BuildRecommendation(
            probe: null,
            runtimeUrl: "http://localhost:11434/api/generate");

        Assert.Equal("nvidia", recommendation.ProfileKey);
        Assert.True(recommendation.UsesGpu);
        Assert.Contains("RTX 3050", recommendation.GpuLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(recommendation.Reasons, reason =>
            reason.Contains("telemetria do host", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void build_recommendation_must_use_host_amd_gpu_when_browser_probe_is_empty()
    {
        var hardware = new HostTelemetrySnapshot
        {
            Source = "sidecar",
            Gpus =
            [
                new HostGpuTelemetry
                {
                    Vendor = "amd",
                    Name = "AMD Radeon RX 7800 XT"
                }
            ]
        };

        var advisor = new RuntimeSetupAdvisor(
            "Test shell",
            new FakeHardwareProbe(hardware));

        var recommendation = advisor.BuildRecommendation(
            probe: null,
            runtimeUrl: "http://localhost:11434/api/generate");

        Assert.Equal("amd", recommendation.ProfileKey);
        Assert.True(recommendation.UsesGpu);
        Assert.Contains("Radeon", recommendation.GpuLabel, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeHardwareProbe(HostTelemetrySnapshot snapshot) : IHostHardwareProbe
    {
        public HostTelemetrySnapshot Probe() => snapshot;
    }
}
