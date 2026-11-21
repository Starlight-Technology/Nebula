using AngleSharp.Dom;

using Bunit;

using Corona.Theming;

using Microsoft.Extensions.DependencyInjection;

using Nebula.Agent;
using Nebula.App.Shared.Pages;
using Nebula.App.Shared.Setup;
using Nebula.App.Shared.State;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Llama.Client;

namespace Nebula.App.Test;

public abstract class HomePageTestContext : BunitContext
{
    protected HomePageTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddCoronaTheming(CoronaThemes.Light());
    }

    protected FakeSandboxSettingsStore RegisterPageServices(
        IManager manager,
        ILlamaClient llamaClient,
        IRuntimeSetupAdvisor? advisor = null,
        FakeSandboxSettingsStore? sandboxSettingsStore = null,
        SandboxMode sandboxModeFloor = SandboxMode.Disabled)
    {
        var store = sandboxSettingsStore ?? new FakeSandboxSettingsStore();
        Services.AddSingleton(manager);
        Services.AddSingleton(llamaClient);
        Services.AddSingleton(advisor ?? new RuntimeSetupAdvisor("Test shell"));
        Services.AddSingleton<ISandboxSettingsStore>(store);
        Services.AddScoped(_ => new NebulaRuntimeSettings
        {
            MainModel = llamaClient.SelectedModel,
            LearningModel = llamaClient.SelectedModel,
            SandboxMode = sandboxModeFloor,
            SandboxModeFloor = sandboxModeFloor
        });
        Services.AddScoped<NebulaWorkspaceState>();

        var module = JSInterop.SetupModule("./_content/Nebula.App.Shared/nebula-runtime.js");
        module
            .Setup<ClientEnvironmentProbe>("getClientEnvironment")
            .SetResult(new ClientEnvironmentProbe
            {
                Platform = "Win32",
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/136.0",
                BrowserName = "Chrome",
                BrowserVersion = "136.0.0.0",
                GpuVendor = "NVIDIA",
                GpuRenderer = "Mock RTX",
                WebGlSupported = true,
                ViewportWidth = 1440,
                ViewportHeight = 900
            });

        return store;
    }

    protected static IElement FindButton(IRenderedComponent<Chat> component, string label)
    {
        return component.FindAll("button")
            .Single(button => button.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class FakeSandboxSettingsStore : ISandboxSettingsStore
{
    private readonly Dictionary<(string UserId, string Workspace), SandboxSettings> rows = [];

    public List<SandboxSettings> SavedSettings { get; } = [];

    public Task<SandboxSettings?> GetEffectiveAsync(
        string userId,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        var normalizedWorkspace = workspace?.Trim() ?? string.Empty;
        if (rows.TryGetValue((userId, normalizedWorkspace), out var exact))
        {
            return Task.FromResult<SandboxSettings?>(exact);
        }

        if (normalizedWorkspace.Length > 0 &&
            rows.TryGetValue((userId, string.Empty), out var global))
        {
            return Task.FromResult<SandboxSettings?>(global);
        }

        return Task.FromResult<SandboxSettings?>(null);
    }

    public Task SaveAsync(
        SandboxSettings settings,
        CancellationToken cancellationToken = default)
    {
        var workspace = settings.Workspace?.Trim() ?? string.Empty;
        rows[(settings.UserId, workspace)] = settings;
        SavedSettings.Add(settings);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string userId,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        rows.Remove((userId, workspace?.Trim() ?? string.Empty));
        return Task.CompletedTask;
    }

    public void Seed(SandboxSettings settings)
    {
        var workspace = settings.Workspace?.Trim() ?? string.Empty;
        rows[(settings.UserId, workspace)] = settings;
    }
}
