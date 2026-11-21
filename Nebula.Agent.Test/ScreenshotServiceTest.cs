using Microsoft.Extensions.Options;
using Moq;
using Nebula.Agent.Infrastructure;
using Nebula.Core.Agent;
using Nebula.Core.Configuration;
using Nebula.Runner;
using SkiaSharp;

namespace Nebula.Agent.Test;

public sealed class ScreenshotServiceTest
{
    [Fact]
    public async Task capture_must_fail_when_screenshots_are_disabled()
    {
        var service = CreateService(enabled: false);

        var result = await service.CaptureAsync(
            new ScreenshotCaptureRequest("http://localhost:5166"));

        Assert.False(result.Success);
        Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task compare_must_report_no_changes_for_identical_images()
    {
        var service = CreateService();
        var image = CreatePng(SKColors.White);

        var result = await service.CompareAsync(image, image);

        Assert.Null(result.Error);
        Assert.Equal(0, result.DifferencePercent);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task compare_must_report_changes_for_different_images()
    {
        var service = CreateService();
        var baseline = CreatePng(SKColors.Black);
        var current = CreatePng(SKColors.White);

        var result = await service.CompareAsync(baseline, current);

        Assert.Null(result.Error);
        Assert.Equal(100, result.DifferencePercent);
        Assert.True(result.HasChanges);
        Assert.NotNull(result.DiffImage);
    }

    [Fact]
    public async Task baseline_roundtrip_must_use_explicit_root()
    {
        using var workspace = new TempTestWorkspace();
        var service = CreateService();
        var image = CreatePng(SKColors.CornflowerBlue);
        var request = new ScreenshotCaptureRequest(
            "http://localhost:5166",
            Viewport: "1280x720");

        await service.SaveBaselineAsync(
            "project",
            "home",
            request,
            image,
            workspace.Path);

        var baseline = await service.GetBaselineAsync(
            "project",
            "home",
            workspace.Path);

        Assert.NotNull(baseline);
        Assert.True(baseline.Success);
        Assert.Equal(image, baseline.ImageData);
        Assert.True(
            File.Exists(Path.Combine(workspace.Path, "project", "home.png")));
    }

    private static ScreenshotService CreateService(bool enabled = true)
    {
        return new ScreenshotService(
            Options.Create(new ScreenshotOptions { Enabled = enabled }),
            new NebulaRuntimeSettings
            {
                ScreenshotEnabled = enabled,
                ScreenshotDiffThresholdPercent = 0.1
            },
            new TestLogger(),
            Mock.Of<IShellExecutor>());
    }

    private static byte[] CreatePng(SKColor color, int size = 8)
    {
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class TestLogger : Nebula.Agent.ILogger
    {
        public void Log(string message)
        {
        }

        public void LogError(string message)
        {
        }
    }
}
