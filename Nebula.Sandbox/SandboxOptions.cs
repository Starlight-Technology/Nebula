using Microsoft.Extensions.Configuration;

using Nebula.Core.Configuration;

namespace Nebula.Sandbox;

public sealed class SandboxOptions
{
    public string Token { get; init; } = string.Empty;

    public SandboxContainerRuntime Runtime { get; init; } =
        SandboxContainerRuntime.Auto;

    public string ContainerTempRoot { get; init; } = "/tmp";

    public string HostTempRoot { get; init; } = string.Empty;

    public int MaxTimeoutSeconds { get; init; } = 300;

    public int MaxOutputChars { get; init; } = 1_000_000;

    public static SandboxOptions FromEnvironment() =>
        FromConfiguration(
            new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build());

    public static SandboxOptions FromConfiguration(IConfiguration configuration)
    {
        return new SandboxOptions
        {
            Token = Read(configuration, "SANDBOX_TOKEN", string.Empty),
            Runtime = NebulaRuntimeSettings.ParseSandboxRuntime(
                configuration["SANDBOX_RUNTIME"]),
            ContainerTempRoot = Read(
                configuration,
                "SANDBOX_CONTAINER_TEMP_ROOT",
                "/tmp"),
            HostTempRoot = Read(
                configuration,
                "SANDBOX_HOST_TEMP_ROOT",
                string.Empty),
            MaxTimeoutSeconds = ReadInt(
                configuration,
                "SANDBOX_MAX_TIMEOUT_SECONDS",
                300,
                1,
                3600),
            MaxOutputChars = ReadInt(
                configuration,
                "SANDBOX_MAX_OUTPUT_CHARS",
                1_000_000,
                1_000,
                20_000_000)
        };
    }

    private static string Read(
        IConfiguration configuration,
        string name,
        string fallback)
    {
        var value = configuration[name];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ReadInt(
        IConfiguration configuration,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        var value = configuration[name];
        return int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }
}
