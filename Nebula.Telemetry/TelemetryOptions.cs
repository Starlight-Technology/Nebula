namespace Nebula.Telemetry;

public sealed class TelemetryOptions
{
    public string ProcRoot { get; init; } = "/proc";

    public string SysRoot { get; init; } = "/sys";

    public string HostRoot { get; init; } = "/";

    public string? DockerSocket { get; init; }

    public string OllamaContainerName { get; init; } = "ollama";

    public int IntervalMilliseconds { get; init; } = 1000;

    public static TelemetryOptions FromEnvironment()
    {
        return new TelemetryOptions
        {
            ProcRoot = Read("TELEMETRY_PROC_ROOT", "/proc"),
            SysRoot = Read("TELEMETRY_SYS_ROOT", "/sys"),
            HostRoot = Read("TELEMETRY_HOST_ROOT", "/"),
            DockerSocket = NullIfWhiteSpace(Environment.GetEnvironmentVariable("TELEMETRY_DOCKER_SOCKET")),
            OllamaContainerName = Read("OLLAMA_CONTAINER_NAME", "ollama"),
            IntervalMilliseconds = ReadInt("TELEMETRY_INTERVAL_MS", 1000, 250, 60_000)
        };
    }

    private static string Read(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ReadInt(string name, int fallback, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
