namespace Nebula.Llama.Client;

public interface ILlamaRuntimeTelemetryService
{
    Task<LlamaRuntimeTelemetrySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class LlamaRuntimeTelemetrySnapshot
{
    public bool IsAvailable { get; init; }

    public string StatusLabel { get; init; } = "Ollama offline";

    public string StatusDetail { get; init; } = "Sem leitura de runtime.";

    public string RuntimeModeLabel { get; init; } = "CPU runtime";

    public string RuntimeModeDetail { get; init; } = "Sem aceleracao de GPU detectada.";

    public LlamaRuntimeMetric Cpu { get; init; } = LlamaRuntimeMetric.Unavailable("Sem leitura de CPU.");

    public LlamaRuntimeMetric Memory { get; init; } = LlamaRuntimeMetric.Unavailable("Sem leitura de memoria.");

    public LlamaRuntimeMetric Gpu { get; init; } = LlamaRuntimeMetric.Unavailable("Sem leitura de GPU.");

    public string ContainerName { get; init; } = "ollama";

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public string? LastError { get; init; }
}

public sealed class LlamaRuntimeMetric
{
    public static LlamaRuntimeMetric Unavailable(string detailValue)
    {
        return new LlamaRuntimeMetric
        {
            ShortValue = "--",
            DetailValue = detailValue,
            IsAvailable = false
        };
    }

    public string ShortValue { get; init; } = "--";

    public string DetailValue { get; init; } = string.Empty;

    public double? Percent { get; init; }

    public bool IsAvailable { get; init; }
}

public interface IRuntimeCommandRunner
{
    Task<RuntimeCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed record RuntimeCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
