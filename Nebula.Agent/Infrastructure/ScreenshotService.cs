using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Nebula.Core.Agent;
using Nebula.Runner;
using SkiaSharp;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nebula.Agent.Infrastructure;

public sealed class ScreenshotService(
    IOptions<ScreenshotOptions> options,
    ILogger logger,
    IShellExecutor executor) : IScreenshotService
{
    private readonly ScreenshotOptions _options = options.Value;
    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    private IPlaywright? _playwright;
    IBrowser? _browser;
    private readonly ILogger logger = logger;
    private readonly IShellExecutor executor = executor;

    public async Task<ScreenshotResult> CaptureAsync(ScreenshotCaptureRequest request, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new ScreenshotResult(false, null, null, "Screenshot capture is disabled", request.Url, request.Viewport, DateTimeOffset.UtcNow);
        }

        await _captureSemaphore.WaitAsync(cancellationToken);
        try
        {
            await EnsureBrowserAsync(cancellationToken);
            if (_browser == null)
            {
                return new ScreenshotResult(false, null, null, "Browser not available", request.Url, request.Viewport, DateTimeOffset.UtcNow);
            }

            var page = await _browser!.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = ParseViewport(request.Viewport)
            });

            page.SetDefaultTimeout(_options.DefaultTimeoutMs);

            var response = await page.GotoAsync(request.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = _options.DefaultTimeoutMs
            });

            if (response == null || !response.Ok)
            {
                return new ScreenshotResult(false, null, null, $"Failed to navigate: {response?.Status}", request.Url, request.Viewport, DateTimeOffset.UtcNow);
            }

            if (request.DelayMs > 0)
            {
                await page.WaitForTimeoutAsync(request.DelayMs);
            }

            if (!string.IsNullOrWhiteSpace(request.Selector))
            {
                try
                {
                    await page.WaitForSelectorAsync(request.Selector, new PageWaitForSelectorOptions { Timeout = 5000 });
                }
                catch
                {
                    logger.Log($"[SCREENSHOT] Selector '{request.Selector}' not found on {request.Url}");
                }
            }

            byte[] screenshotBytes;
            if (request.FullPage)
            {
                screenshotBytes = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
            }
            else if (!string.IsNullOrWhiteSpace(request.Selector))
            {
                var element = await page.QuerySelectorAsync(request.Selector);
                if (element != null)
                {
                    screenshotBytes = await element.ScreenshotAsync();
                }
                else
                {
                    screenshotBytes = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });
                }
            }
            else
            {
                screenshotBytes = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = false });
            }

            var hash = ComputeHash(screenshotBytes);
            var projectKey = request.ProjectKey ?? await GetProjectKeyFromUrl(request.Url);
            var name = request.Name ?? GenerateNameFromUrl(request.Url);

            await SaveBaselineAsync(projectKey, name, request, screenshotBytes, cancellationToken);

            return new ScreenshotResult(true, screenshotBytes, hash, null, request.Url, request.Viewport, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[SCREENSHOT] Capture failed for {request.Url}: {ex.Message}");
            return new ScreenshotResult(false, null, null, ex.Message, request.Url, request.Viewport, DateTimeOffset.UtcNow);
        }
        finally
        {
            _captureSemaphore.Release();
        }
    }

    public async Task<VisualDiffResult> CompareAsync(byte[] baseline, byte[] current, CancellationToken cancellationToken = default)
    {
        try
        {
            using var baselineBitmap = SKBitmap.Decode(baseline);
            using var currentBitmap = SKBitmap.Decode(current);

            if (baselineBitmap == null || currentBitmap == null)
            {
                return new VisualDiffResult(false, 0, null, baseline, current, null, null, "Failed to decode images");
            }

            var width = Math.Max(baselineBitmap.Width, currentBitmap.Width);
            var height = Math.Max(baselineBitmap.Height, currentBitmap.Height);

            using var diffBitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(diffBitmap);
            canvas.Clear(SKColors.White);

            var paint = new SKPaint { IsAntialias = true };

            if (baselineBitmap.Width == currentBitmap.Width && baselineBitmap.Height == currentBitmap.Height)
            {
                canvas.DrawBitmap(baselineBitmap, 0, 0);
                canvas.DrawBitmap(currentBitmap, 0, 0, paint);
            }
            else
            {
                canvas.DrawBitmap(baselineBitmap, 0, 0);
                canvas.DrawBitmap(currentBitmap, 0, 0, paint);
            }

            var diffPercent = CalculatePixelDifference(baselineBitmap, currentBitmap);
            var hasChanges = diffPercent > _options.DiffThresholdPercent;

            using var diffImage = SKImage.FromBitmap(diffBitmap);
            using var diffData = diffImage.Encode(SKEncodedImageFormat.Png, 100);
            var diffBytes = diffData.ToArray();

            var baselineHash = ComputeHash(baseline);
            var currentHash = ComputeHash(current);

            return new VisualDiffResult(
                hasChanges,
                diffPercent,
                diffBytes,
                baseline,
                current,
                baselineHash,
                currentHash,
                null);
        }
        catch (Exception ex)
        {
            logger.Log($"[SCREENSHOT] Diff failed: {ex.Message}");
            return new VisualDiffResult(false, 0, null, baseline, current, null, null, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ScreenshotBaseline>> GetBaselinesAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        var baselines = new List<ScreenshotBaseline>();
        var projectDir = GetProjectBaselineDir(projectKey);

        if (!Directory.Exists(projectDir))
        {
            return baselines;
        }

        var manifest = await GetManifestAsync(projectKey, cancellationToken);
        if (manifest == null)
        {
            return baselines;
        }

        foreach (var entry in manifest.Screenshots)
        {
            var filePath = Path.Combine(projectDir, $"{entry.Key}.png");
            if (File.Exists(filePath))
            {
                var info = new FileInfo(filePath);
                baselines.Add(new ScreenshotBaseline(
                    entry.Key,
                    entry.Value.Hash,
                    entry.Value.Url,
                    entry.Value.Viewport,
                    entry.Value.UpdatedAt,
                    info.Length));
            }
        }

        return baselines;
    }

    public async Task<ScreenshotResult?> GetBaselineAsync(string projectKey, string name, CancellationToken cancellationToken = default)
    {
        var projectDir = GetProjectBaselineDir(projectKey);
        var filePath = Path.Combine(projectDir, $"{name}.png");

        if (!File.Exists(filePath))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var hash = ComputeHash(bytes);

        return new ScreenshotResult(true, bytes, hash, null, "", "", DateTimeOffset.UtcNow);
    }

    public async Task SaveBaselineAsync(string projectKey, string name, ScreenshotCaptureRequest request, byte[] imageData, CancellationToken cancellationToken = default)
    {
        var projectDir = GetProjectBaselineDir(projectKey);
        Directory.CreateDirectory(projectDir);

        var filePath = Path.Combine(projectDir, $"{name}.png");
        await File.WriteAllBytesAsync(filePath, imageData, cancellationToken);

        var hash = ComputeHash(imageData);

        var manifest = await GetManifestAsync(projectKey, cancellationToken) ?? new ScreenshotManifest(projectKey, new Dictionary<string, ScreenshotManifestEntry>());
        manifest.Screenshots[name] = new ScreenshotManifestEntry(
            hash,
            request.Url,
            request.Viewport,
            DateTimeOffset.UtcNow);

        await SaveManifestAsync(projectKey, manifest, cancellationToken);

        logger.Log($"[SCREENSHOT] Saved baseline '{name}' for project '{projectKey}' (hash: {hash[..8]})");
    }

    public async Task DeleteBaselineAsync(string projectKey, string name, CancellationToken cancellationToken = default)
    {
        var projectDir = GetProjectBaselineDir(projectKey);
        var filePath = Path.Combine(projectDir, $"{name}.png");

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var manifest = await GetManifestAsync(projectKey, cancellationToken);
        if (manifest != null && manifest.Screenshots.Remove(name))
        {
            await SaveManifestAsync(projectKey, manifest, cancellationToken);
        }
    }

    public async Task<ScreenshotManifest?> GetManifestAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        var projectDir = GetProjectBaselineDir(projectKey);
        var manifestPath = Path.Combine(projectDir, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            return new ScreenshotManifest(projectKey, new Dictionary<string, ScreenshotManifestEntry>());
        }

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            return JsonSerializer.Deserialize<ScreenshotManifest>(json);
        }
        catch
        {
            return new ScreenshotManifest(projectKey, new Dictionary<string, ScreenshotManifestEntry>());
        }
    }

    public async Task SaveManifestAsync(string projectKey, ScreenshotManifest manifest, CancellationToken cancellationToken = default)
    {
        var projectDir = GetProjectBaselineDir(projectKey);
        Directory.CreateDirectory(projectDir);

        var manifestPath = Path.Combine(projectDir, "manifest.json");
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
    }

    private async Task EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser != null && _browser.IsConnected)
        {
            return;
        }

        try
        {
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
            });

            logger.Log($"[SCREENSHOT] Browser launched ({_options.BrowserChannel})");
        }
        catch (Exception ex)
        {
            logger.Log($"[SCREENSHOT] Failed to launch browser: {ex.Message}");
            _browser = null;
        }
    }

    private static ViewportSize ParseViewport(string viewport)
    {
        var parts = viewport.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
        {
            return new ViewportSize { Width = width, Height = height };
        }
        return new ViewportSize { Width = 1920, Height = 1080 };
    }

    private static string GenerateNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.Trim('/').Replace('/', '_');
            return string.IsNullOrWhiteSpace(path) ? "homepage" : path;
        }
        catch
        {
            return "page";
        }
    }

    private async Task<string> GetProjectKeyFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var workspaceRoot = Environment.CurrentDirectory;
            return await GetProjectKeyFromPath(workspaceRoot);
        }
        catch
        {
            return "default";
        }
    }

    private async Task<string> GetProjectKeyFromPath(string path)
    {
        try
        {
            var result = await executor.RunCommandAsync($"git -C \"{path}\" rev-parse --show-toplevel 2>/dev/null || echo \"{path}\"", CancellationToken.None);
            var repoRoot = result?.Trim() ?? path;
            return ComputeHash(repoRoot)[..12];
        }
        catch
        {
            return ComputeHash(path)[..12];
        }
    }

    private string GetProjectBaselineDir(string projectKey)
    {
        var basePath = Path.IsPathRooted(_options.BaselineStoragePath)
            ? _options.BaselineStoragePath
            : Path.Combine(Environment.CurrentDirectory, _options.BaselineStoragePath);
        return Path.Combine(basePath, projectKey);
    }

    private static string ComputeHash(byte[] data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeHash(string data)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private double CalculatePixelDifference(SKBitmap baseline, SKBitmap current)
    {
        if (baseline.Width != current.Width || baseline.Height != current.Height)
        {
            return 100.0;
        }

        var totalPixels = baseline.Width * baseline.Height;
        var differentPixels = 0;
        const int threshold = 30;

        for (int y = 0; y < baseline.Height; y++)
        {
            for (int x = 0; x < baseline.Width; x++)
            {
                var bp = baseline.GetPixel(x, y);
                var cp = current.GetPixel(x, y);

                var dr = Math.Abs(bp.Red - cp.Red);
                var dg = Math.Abs(bp.Green - cp.Green);
                var db = Math.Abs(bp.Blue - cp.Blue);

                if (dr > threshold || dg > threshold || db > threshold)
                {
                    differentPixels++;
                }
            }
        }

        return (double)differentPixels / totalPixels * 100.0;
    }

    public void Dispose()
    {
        _browser?.CloseAsync().GetAwaiter().GetResult();
        _playwright?.Dispose();
        _captureSemaphore.Dispose();
    }
}
