using Nebula.Llama.Client;

namespace Nebula.Agent.Test;

public class LlamaRuntimeTelemetryServiceTest
{
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

        var service = new LlamaRuntimeTelemetryService(runner);

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

        var service = new LlamaRuntimeTelemetryService(runner);

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

        var service = new LlamaRuntimeTelemetryService(runner);

        var snapshot = await service.GetSnapshotAsync();

        Assert.True(snapshot.IsAvailable);
        Assert.Equal("CPU runtime", snapshot.RuntimeModeLabel);
        Assert.False(snapshot.Gpu.IsAvailable);
        Assert.Contains("CPU", snapshot.Gpu.DetailValue, StringComparison.OrdinalIgnoreCase);
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
