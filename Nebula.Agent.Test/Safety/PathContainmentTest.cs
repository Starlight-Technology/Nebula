using Nebula.Core.Safety;

namespace Nebula.Agent.Test.Safety;

public sealed class PathContainmentTest : IDisposable
{
    private readonly string workspace = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"nebula-ws-{Guid.NewGuid():N}")).FullName;
    private readonly string outside = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"nebula-out-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void is_under_must_allow_child_inside_root()
    {
        Assert.True(PathContainment.IsUnder(
            Path.Combine(workspace, "sub", "file.txt"),
            workspace));
    }

    [Fact]
    public void is_under_must_allow_root_itself()
    {
        Assert.True(PathContainment.IsUnder(workspace, workspace));
    }

    [Fact]
    public void is_under_must_block_dotdot_escape()
    {
        Assert.False(PathContainment.IsUnder(
            Path.Combine(workspace, "..", "escape.txt"),
            workspace));
    }

    [Fact]
    public void is_under_must_block_absolute_outside_path()
    {
        Assert.False(PathContainment.IsUnder(outside, workspace));
    }

    [Fact]
    public void is_under_must_reject_null_or_empty()
    {
        Assert.False(PathContainment.IsUnder(string.Empty, workspace));
        Assert.False(PathContainment.IsUnder(workspace, string.Empty));
    }

    [Fact]
    public void is_under_must_block_symlink_escaping_workspace()
    {
        var link = Path.Combine(workspace, "evil-link");
        File.CreateSymbolicLink(link, Path.Combine(outside, "secret.txt"));

        Assert.False(PathContainment.IsUnder(link, workspace));
    }

    [Fact]
    public void is_under_must_allow_symlink_pointing_inside_workspace()
    {
        var target = Path.Combine(workspace, "real.txt");
        File.WriteAllText(target, "data");
        var link = Path.Combine(workspace, "good-link");
        File.CreateSymbolicLink(link, target);

        Assert.True(PathContainment.IsUnder(link, workspace));
    }

    [Fact]
    public void is_under_must_block_symlinked_directory_escaping_workspace()
    {
        var linkDir = Path.Combine(workspace, "evil-dir");
        Directory.CreateSymbolicLink(linkDir, outside);

        Assert.False(PathContainment.IsUnder(
            Path.Combine(linkDir, "file.txt"),
            workspace));
    }

    public void Dispose()
    {
        try { Directory.Delete(workspace, recursive: true); } catch { }
        try { Directory.Delete(outside, recursive: true); } catch { }
    }
}
