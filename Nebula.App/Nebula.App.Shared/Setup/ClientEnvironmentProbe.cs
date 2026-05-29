namespace Nebula.App.Shared.Setup;

public sealed class ClientEnvironmentProbe
{
    public string Platform { get; set; } = string.Empty;

    public string UserAgent { get; set; } = string.Empty;

    public string BrowserName { get; set; } = string.Empty;

    public string BrowserVersion { get; set; } = string.Empty;

    public string GpuVendor { get; set; } = string.Empty;

    public string GpuRenderer { get; set; } = string.Empty;

    public bool WebGlSupported { get; set; }

    public int MaxTouchPoints { get; set; }

    public int ViewportWidth { get; set; }

    public int ViewportHeight { get; set; }
}
