using AngleSharp.Dom;

using Bunit;

using Corona.Theming;

using Microsoft.Extensions.DependencyInjection;

using Nebula.Agent;
using Nebula.App.Shared.Pages;
using Nebula.App.Shared.Setup;
using Nebula.App.Shared.State;
using Nebula.Llama.Client;

namespace Nebula.App.Test;

public abstract class HomePageTestContext : TestContext
{
    protected HomePageTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddCoronaTheming(CoronaThemes.Light());
    }

    protected void RegisterPageServices(IManager manager, ILlamaClient llamaClient, IRuntimeSetupAdvisor? advisor = null)
    {
        Services.AddSingleton(manager);
        Services.AddSingleton(llamaClient);
        Services.AddSingleton(advisor ?? new RuntimeSetupAdvisor("Test shell"));
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
    }

    protected static IElement FindButton(IRenderedComponent<Chat> component, string label)
    {
        return component.FindAll("button")
            .Single(button => button.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase));
    }
}
