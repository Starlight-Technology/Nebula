using Nebula.Core.Projects;

namespace Nebula.Agent.Test.Projects;

public sealed class ReferenceWorkspaceTest
{
    [Fact]
    public void resolve_must_create_requested_folder_when_missing()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Nebula",
            "tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = ReferenceWorkspace.Resolve(root);

            Assert.True(Directory.Exists(workspace.Root));
            Assert.Equal(
                System.IO.Path.GetFullPath(root),
                workspace.Root);
            Assert.True(workspace.IsNew);
            Assert.True(workspace.IsEmpty);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void resolve_must_use_existing_folder_as_is()
    {
        using var workspace = new ReferenceWorkspaceTestFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(workspace.Path, "src"));

        var resolved = ReferenceWorkspace.Resolve(workspace.Path);

        Assert.Equal(System.IO.Path.GetFullPath(workspace.Path), resolved.Root);
        Assert.False(resolved.IsNew);
        Assert.False(resolved.IsEmpty);
    }

    [Fact]
    public void resolve_must_create_fresh_empty_workspace_when_not_specified()
    {
        var workspace = ReferenceWorkspace.Resolve(null);

        Assert.True(Directory.Exists(workspace.Root));
        Assert.EndsWith(
            ReferenceWorkspace.DefaultWorkspaceFolderName,
            workspace.Root,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(workspace.IsEmpty);
    }

    [Fact]
    public void resolve_must_ignore_whitespace_requested_root()
    {
        var workspace = ReferenceWorkspace.Resolve("   ");

        Assert.True(Directory.Exists(workspace.Root));
        Assert.EndsWith(
            ReferenceWorkspace.DefaultWorkspaceFolderName,
            workspace.Root,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // best effort cleanup
        }
    }

    private sealed class ReferenceWorkspaceTestFolder : IDisposable
    {
        public ReferenceWorkspaceTestFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Nebula",
                "tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            TryDelete(Path);
        }
    }
}
