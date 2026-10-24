using Bunit;

using Nebula.App.Shared.Components;
using Nebula.Core.Agent;

namespace Nebula.App.Test;

public sealed class CoronaComponentUsageTest : HomePageTestContext
{
    [Fact]
    public void artifact_panel_renders_corona_buttons_with_titles()
    {
        var runId = Guid.NewGuid();
        var artifact = new AgentArtifactRecord(
            Guid.NewGuid(),
            runId,
            "hello.cs",
            "/tmp/hello.cs",
            null,
            DateTimeOffset.UtcNow);

        var cut = Render<ArtifactPanel>(parameters => parameters
            .Add(panel => panel.Artifacts, [artifact]));

        Assert.Equal(
            "Copiar caminho",
            cut.Find("button[title='Copiar caminho']").GetAttribute("title"));
        Assert.Equal(
            "Abrir arquivo",
            cut.Find("button[title='Abrir arquivo']").GetAttribute("title"));
    }
}
