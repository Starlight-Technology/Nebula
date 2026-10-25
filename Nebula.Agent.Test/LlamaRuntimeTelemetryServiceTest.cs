using Nebula.Core.Telemetry;
using Nebula.Llama.Client;
using Nebula.Llama.Client.Telemetry;

namespace Nebula.Agent.Test;

public class LlamaRuntimeTelemetryServiceTest
{

    [Fact]
    public async Task get_snapshot_async_must_use_sidecar_hardware_and_disk()
    {
        var runner = new FakeRuntimeCommandRunner();
        runner.Add(
            "docker",
            ["stats", "--no-stream", "--format", "{{json .}}", "ollama"],
            new RuntimeCommandResult(1, string.Empty, "No such container: ollama"));

        var hardware = new HostTelemetrySnapshot
        {
            Source = "sidecar",
            Cpu = new HostCpuTelemetry
            {
                Model = "12th Gen Intel(R) Core(TM) i7-12700H",
                LogicalCores = 20,
                UsagePercent = 18.4,
                Detail = "12th Gen Intel(R) Core(TM) i7-12700H | 20 threads"
            },
            Memory = new HostMemoryTelemetry
            {
                TotalBytes = 24L * 1024 * 1024 * 1024,
                UsedBytes = 12L * 1024 * 1024 * 1024,
                AvailableBytes = 12L * 1024 * 1024 * 1024,
                UsagePercent = 50,
                Detail = "12.0 GiB / 24.0 GiB (50%)"
            },
            Gpus =
            [
                new HostGpuTelemetry
                {
                    Vendor = "nvidia",
                    Name = "NVIDIA GeForce RTX 3050 Laptop GPU",
                    UtilizationPercent = 42,
                    MemoryUsedBytes = 3530L * 1024 * 1024,
                    MemoryTotalBytes = 4096L * 1024 * 1024,
                    Source = "nvidia-smi"
                }
            ],
            Disks =
            [
                new HostDiskTelemetry
                {
                    Name = "overlay",
                    MountPoint = "/host/root",
                    UsedBytes = 731L * 1024 * 1024 * 1024,
                    TotalBytes = 952L * 1024 * 1024 * 1024,
                    FreeBytes = 215L * 1024 * 1024 * 1024,
                    UsagePercent = 76.8,
                    IsHost = true
                }
            ]
        };

        var service = new LlamaRuntimeTelemetryService(
            runner,
            hardwareProbe: new FakeHardwareProbe(new HostTelemetrySnapshot()),
            telemetrySource: new FakeTelemetrySource(hardware));

        var snapshot = await service.GetSnapshotAsync();

        Assert.False(snapshot.IsAvailable);
        Assert.Equal("sidecar", snapshot.MetricsSource);
        Assert.Contains("i7-12700H", snapshot.Cpu.DetailValue, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("76.8%", snapshot.Disk.ShortValue, StringComparison.OrdinalIgnoreCase);
        Assert.Single(snapshot.Gpus);
        Assert.Contains("RTX 3050", snapshot.Gpu.DetailValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task get_snapshot_async_must_parse_docker_and_nvidia_metrics()
    {
        var runner = new FakeRuntimeCommandRunner();
        runner.Add(
            "docker",
            ["stats", "--no-stream", "--format", "{{json .}}", "ollama"],
            new RuntimeCommandResult(
                0,
                """{"CPUPerc":"12.45%","MemPerc":"34.10%","MemUsage":"3.52GiB / 11.49GiB","Name":"ollama"}""",
                string.Empty));
        runner.Add(
            "docker",
            ["inspect", "--format", "{{json .Config.Env}}", "ollama"],
            new RuntimeCommandResult(
                0,
                """["OLLAMA_ACCELERATION_MODE=nvidia-cuda","NVIDIA_VISIBLE_DEVICES=all"]""",
                string.Empty));
        runner.Add(
            "nvidia-smi",
            ["--query-gpu=name,utilization.gpu,memory.used,memory.total", "--format=csv,noheader,nounits"],
            new RuntimeCommandResult(
                0,
                "NVIDIA GeForce RTX 3050 Laptop GPU, 39, 3530, 4096",
                string.Empty));

        var service = new LlamaRuntimeTelemetryService(
            runner,
            hardwareProbe: new FakeHardwareProbe(new HostTelemetrySnapshot()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.True(snapshot.IsAvailable);
        Assert.Equal("Ollama online", snapshot.StatusLabel);
        Assert.Equal("NVIDIA CUDA", snapshot.RuntimeModeLabel);
        Assert.Equal("12.5%", snapshot.Cpu.ShortValue);
        Assert.Equal("3.5 GiB", snapshot.Memory.ShortValue);
        Assert.Equal("39% | 3.4 GiB", snapshot.Gpu.ShortValue);
        Assert.Contains("RTX 3050", snapshot.Gpu.DetailValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task get_snapshot_async_must_fallback_to_offline_state_when_docker_stats_fails()
    {
        var runner = new FakeRuntimeCommandRunner();
        runner.Add(
            "docker",
            ["stats", "--no-stream", "--format", "{{json .}}", "ollama"],
            new RuntimeCommandResult(1, string.Empty, "No such container: ollama"));

        var service = new LlamaRuntimeTelemetryService(
            runner,
            hardwareProbe: new FakeHardwareProbe(new HostTelemetrySnapshot()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.False(snapshot.IsAvailable);
        Assert.Equal("Ollama offline", snapshot.StatusLabel);
        Assert.Equal("--", snapshot.Cpu.ShortValue);
        Assert.Contains("No such container", snapshot.StatusDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task get_snapshot_async_must_report_cpu_mode_without_gpu_reading()
    {
        var runner = new FakeRuntimeCommandRunner();
        runner.Add(
            "docker",
            ["stats", "--no-stream", "--format", "{{json .}}", "ollama"],
            new RuntimeCommandResult(
                0,
                """{"CPUPerc":"4.00%","MemPerc":"1.25%","MemUsage":"256.00MiB / 8.00GiB","Name":"ollama"}""",
                string.Empty));
        runner.Add(
            "docker",
            ["inspect", "--format", "{{json .Config.Env}}", "ollama"],
            new RuntimeCommandResult(
                0,
                """["OLLAMA_ACCELERATION_MODE=cpu"]""",
                string.Empty));

        var service = new LlamaRuntimeTelemetryService(
            runner,
            hardwareProbe: new FakeHardwareProbe(new HostTelemetrySnapshot()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.True(snapshot.IsAvailable);
        Assert.Equal("CPU runtime", snapshot.RuntimeModeLabel);
        Assert.False(snapshot.Gpu.IsAvailable);
        Assert.Contains("CPU", snapshot.Gpu.DetailValue, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeHardwareProbe(HostTelemetrySnapshot snapshot) : IHostHardwareProbe
    {
        public HostTelemetrySnapshot Probe() => snapshot;
    }

    private sealed class FakeTelemetrySource(HostTelemetrySnapshot snapshot) : ITelemetrySource
    {
        public Task<HostTelemetrySnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<HostTelemetrySnapshot?>(snapshot);
    }

    private sealed class FakeRuntimeCommandRunner : IRuntimeCommandRunner
    {
        private readonly Dictionary<string, RuntimeCommandResult> responses = new(StringComparer.Ordinal);

        public void Add(string fileName, IReadOnlyList<string> arguments, RuntimeCommandResult result)
        {
            responses[BuildKey(fileName, arguments)] = result;
        }

        public Task<RuntimeCommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            if (responses.TryGetValue(BuildKey(fileName, arguments), out var result))
            {
                return Task.FromResult(result);
            }

            throw new InvalidOperationException($"No fake response registered for {fileName} {string.Join(' ', arguments)}.");
        }

        private static string BuildKey(string fileName, IReadOnlyList<string> arguments)
        {
            return $"{fileName}|{string.Join('|', arguments)}";
        }
    }
}
