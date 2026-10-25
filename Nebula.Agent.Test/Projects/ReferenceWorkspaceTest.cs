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
    public void resolve_must_use_default_workspace_when_not_specified()
    {
        // Clean up any existing default workspace to ensure test isolation
        var defaultWorkspaceRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            ReferenceWorkspace.DefaultWorkspaceFolderName);
        TryDelete(defaultWorkspaceRoot);

        var workspace = ReferenceWorkspace.Resolve(null);

        Assert.True(Directory.Exists(workspace.Root));
        Assert.EndsWith(
            ReferenceWorkspace.DefaultWorkspaceFolderName,
            workspace.Root,
            StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void resolve_must_isolate_default_workspace_by_conversation()
    {
        var conversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        var workspace = ReferenceWorkspace.Resolve(null, conversationId);
        var otherWorkspace = ReferenceWorkspace.Resolve(null, otherConversationId);

        try
        {
            Assert.True(Directory.Exists(workspace.Root));
            Assert.True(Directory.Exists(otherWorkspace.Root));
            Assert.EndsWith(
                conversationId.ToString("N"),
                workspace.Root,
                StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(
                otherConversationId.ToString("N"),
                otherWorkspace.Root,
                StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(workspace.Root, otherWorkspace.Root);
        }
        finally
        {
            TryDelete(workspace.Root);
            TryDelete(otherWorkspace.Root);
        }
    }

    [Fact]
    public void resolve_must_keep_explicit_workspace_when_conversation_is_set()
    {
        using var workspace = new ReferenceWorkspaceTestFolder();
        var conversationId = Guid.NewGuid();

        var resolved = ReferenceWorkspace.Resolve(
            workspace.Path,
            conversationId);

        Assert.Equal(
            System.IO.Path.GetFullPath(workspace.Path),
            resolved.Root);
        Assert.False(Directory.Exists(
            System.IO.Path.Combine(
                workspace.Path,
                conversationId.ToString("N"))));
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
