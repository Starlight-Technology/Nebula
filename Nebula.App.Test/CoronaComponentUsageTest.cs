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

    [Fact]
    public void artifact_panel_does_not_treat_diff_named_file_as_screenshot()
    {
        var artifact = new AgentArtifactRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "diff-notes.md",
            "/tmp/diff-notes.md",
            null,
            DateTimeOffset.UtcNow);

        var cut = Render<ArtifactPanel>(parameters => parameters
            .Add(panel => panel.Artifacts, [artifact]));

        Assert.Empty(cut.FindAll("button[title='Ver comparacao']"));
    }

    [Fact]
    public void artifact_panel_renders_comparison_for_screenshot_diff_artifact()
    {
        var artifact = new AgentArtifactRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "screenshot diff home 12.5%",
            "/tmp/project/.nebula/screenshots/home.diff.png",
            null,
            DateTimeOffset.UtcNow);

        var cut = Render<ArtifactPanel>(parameters => parameters
            .Add(panel => panel.Artifacts, [artifact]));

        Assert.Single(cut.FindAll("button[title='Ver comparacao']"));
    }
}
