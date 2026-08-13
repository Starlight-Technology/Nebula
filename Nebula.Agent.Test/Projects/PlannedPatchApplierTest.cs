using Nebula.Core.Projects;
using Nebula.Services.Projects;

namespace Nebula.Agent.Test.Projects;

public sealed class PlannedPatchApplierTest
{
    [Fact]
    public async Task apply_async_must_write_all_files_and_return_relative_paths()
    {
        using var workspace = new TempTestWorkspace();
        var applier = new PlannedPatchApplier(
            workspaceRoot: workspace.Path,
            controlledTempRoot: workspace.Path);

        var result = await applier.ApplyAsync(
            new PlannedPatchRequest(
                "Update the app",
                [
                    new PlannedPatchFile("src/App.cs", "public class App { }"),
                    new PlannedPatchFile("README.md", "# My Project")
                ],
                workspace.Path));

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(2, result.AppliedFiles.Count);
        Assert.Contains(result.AppliedFiles, file => file == "src/App.cs");
        Assert.Contains(result.AppliedFiles, file => file == "README.md");
        Assert.Equal(
            "public class App { }",
            File.ReadAllText(Path.Combine(workspace.Path, "src", "App.cs")));
        Assert.Equal(
            "# My Project",
            File.ReadAllText(Path.Combine(workspace.Path, "README.md")));
    }

    [Fact]
    public async Task apply_async_must_refuse_empty_patch()
    {
        using var workspace = new TempTestWorkspace();
        var applier = new PlannedPatchApplier(
            workspaceRoot: workspace.Path);

        var result = await applier.ApplyAsync(
            new PlannedPatchRequest("Empty patch", [], workspace.Path));

        Assert.False(result.Success);
        Assert.Contains("at least one file", result.Error);
    }

    [Fact]
    public async Task apply_async_must_refuse_target_outside_roots()
    {
        var applier = new PlannedPatchApplier(
            workspaceRoot: @"C:\nebula-workspace",
            controlledTempRoot: @"C:\Temp\Nebula");

        var result = await applier.ApplyAsync(
            new PlannedPatchRequest(
                "Patch outside",
                [new PlannedPatchFile("evil.txt", "x")],
                @"C:\Users\Public\evil"));

        Assert.False(result.Success);
        Assert.Contains("outside", result.Error);
    }

    [Fact]
    public async Task apply_async_must_refuse_file_that_escapes_target_directory()
    {
        using var workspace = new TempTestWorkspace();
        var applier = new PlannedPatchApplier(
            workspaceRoot: workspace.Path,
            controlledTempRoot: workspace.Path);

        var result = await applier.ApplyAsync(
            new PlannedPatchRequest(
                "Traversal patch",
                [new PlannedPatchFile("../evil.txt", "x")],
                workspace.Path));

        Assert.False(result.Success);
        Assert.Contains("escapes", result.Error);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "..", "evil.txt")));
    }

    [Fact]
    public async Task apply_async_must_refuse_absolute_patch_path()
    {
        using var workspace = new TempTestWorkspace();
        var applier = new PlannedPatchApplier(
            workspaceRoot: workspace.Path,
            controlledTempRoot: workspace.Path);

        var result = await applier.ApplyAsync(
            new PlannedPatchRequest(
                "Absolute path patch",
                [new PlannedPatchFile(Path.Combine(workspace.Path, "evil.txt"), "x")],
                workspace.Path));

        Assert.False(result.Success);
        Assert.Contains("not a valid relative path", result.Error);
    }
}
