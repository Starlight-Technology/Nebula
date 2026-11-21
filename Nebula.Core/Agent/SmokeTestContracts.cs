namespace Nebula.Core.Agent;

public sealed record SmokeTestRequest(
    string WorkingDirectory,
    IReadOnlyList<string>? ExplicitUrls = null,
    int TimeoutSeconds = 30);

public sealed record SmokeTestResult(
    bool Success,
    IReadOnlyList<SmokeTestCheck> Checks,
    string? Error);

public sealed record SmokeTestCheck(
    string Name,
    string Url,
    bool Healthy,
    int? StatusCode,
    double ResponseTimeMs,
    string? Error);

public sealed record PortScanResult(
    bool Success,
    IReadOnlyList<PortInfo> Ports,
    string? Error);

public sealed record PortInfo(
    int Port,
    string Protocol,
    bool InUse,
    string? ProcessName,
    int? ProcessId);

public sealed record PortSuggestion(
    int RequestedPort,
    IReadOnlyList<int> AvailableAlternatives);

public interface ISmokeTestService
{
    Task<SmokeTestResult> RunSmokeTestsAsync(SmokeTestRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detect candidate web app URLs for a workspace. When
    /// <paramref name="includeFrameworkDefaults"/> is false only URLs explicitly
    /// declared by the project (launchSettings, compose, package.json, env) are
    /// returned, avoiding generic framework defaults that may not be running.
    /// </summary>
    Task<IReadOnlyList<string>> DetectWebAppUrlsAsync(
        string workingDirectory,
        bool includeFrameworkDefaults = true,
        CancellationToken cancellationToken = default);
}

public interface IPortDetectionService
{
    Task<PortScanResult> ScanPortsAsync(string workingDirectory, IReadOnlyList<int>? portsToCheck = null, CancellationToken cancellationToken = default);
    Task<PortSuggestion> SuggestAlternativePortAsync(int requestedPort, CancellationToken cancellationToken = default);
    Task<bool> IsPortInUseAsync(int port, CancellationToken cancellationToken = default);
}