namespace Nebula.Core.Agent;

public sealed record ScreenshotCaptureRequest(
    string Url,
    string Viewport = "1920x1080",
    string? Selector = null,
    int DelayMs = 1000,
    bool FullPage = true,
    string? ProjectKey = null,
    string? Name = null);

public sealed record ScreenshotResult(
    bool Success,
    byte[]? ImageData,
    string? ContentHash,
    string? Error,
    string? Url,
    string Viewport,
    DateTimeOffset CapturedAt);

public sealed record VisualDiffResult(
    bool HasChanges,
    double DifferencePercent,
    byte[]? DiffImage,
    byte[]? BaselineImage,
    byte[]? CurrentImage,
    string? BaselineHash,
    string? CurrentHash,
    string? Error);

public sealed record ScreenshotBaseline(
    string Name,
    string Hash,
    string Url,
    string Viewport,
    DateTimeOffset CapturedAt,
    long SizeBytes);

public sealed record ScreenshotManifest(
    string ProjectKey,
    Dictionary<string, ScreenshotManifestEntry> Screenshots);

public sealed record ScreenshotManifestEntry(
    string Hash,
    string Url,
    string Viewport,
    DateTimeOffset UpdatedAt);

public interface IScreenshotService
{
    Task<ScreenshotResult> CaptureAsync(ScreenshotCaptureRequest request, CancellationToken cancellationToken = default);
    Task<VisualDiffResult> CompareAsync(byte[] baseline, byte[] current, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScreenshotBaseline>> GetBaselinesAsync(string projectKey, CancellationToken cancellationToken = default);
    Task<ScreenshotResult?> GetBaselineAsync(string projectKey, string name, CancellationToken cancellationToken = default);
    Task SaveBaselineAsync(string projectKey, string name, ScreenshotCaptureRequest request, byte[] imageData, CancellationToken cancellationToken = default);
    Task DeleteBaselineAsync(string projectKey, string name, CancellationToken cancellationToken = default);
    Task<ScreenshotManifest?> GetManifestAsync(string projectKey, CancellationToken cancellationToken = default);
    Task SaveManifestAsync(string projectKey, ScreenshotManifest manifest, CancellationToken cancellationToken = default);
}

public sealed class ScreenshotOptions
{
    public bool Enabled { get; set; } = true;
    public string BrowserChannel { get; set; } = "chromium";
    public int DefaultTimeoutMs { get; set; } = 30000;
    public double DiffThresholdPercent { get; set; } = 0.1;
    public string BaselineStoragePath { get; set; } = ".nebula/screenshots/baselines";
    public int MaxConcurrentCaptures { get; set; } = 2;
    public int CaptureDelayMs { get; set; } = 1000;
    public List<string> Viewports { get; set; } = new() { "1920x1080", "1366x768", "375x667" };
}