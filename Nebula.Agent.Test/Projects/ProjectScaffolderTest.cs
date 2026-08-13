using Nebula.Core.Agent;
using Nebula.Services.Projects;

namespace Nebula.Agent.Test.Projects;

public sealed class ProjectScaffolderTest
{
    [Fact]
    public async Task scaffold_async_must_create_all_template_files()
    {
        using var workspace = new TempTestWorkspace();
        var scaffolder = new ProjectScaffolder(
            new ProjectTemplateCatalog(),
            workspaceRoot: workspace.Path);

        var result = await scaffolder.ScaffoldAsync(
            new Nebula.Core.Projects.ProjectScaffoldRequest(
                "dotnet-console",
                workspace.Path));

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal("dotnet-console", result.TemplateId);
        Assert.Equal(6, result.CreatedFiles.Count);
        Assert.Contains(result.CreatedFiles, file => file == "src/App/App.csproj");
        Assert.Contains(result.CreatedFiles, file => file == "src/App/Program.cs");
        Assert.Contains(result.CreatedFiles, file => file == "README.md");
        Assert.Contains(result.VerificationCommands, command => command.StartsWith("dotnet build"));
        Assert.Contains(result.VerificationCommands, command => command.StartsWith("dotnet test"));

        Assert.True(File.Exists(Path.Combine(workspace.Path, "src/App/App.csproj")));
        Assert.True(File.Exists(Path.Combine(workspace.Path, "src/App/Program.cs")));
        Assert.True(File.Exists(Path.Combine(workspace.Path, "tests/App.Tests/UnitTest1.cs")));
    }

    [Fact]
    public async Task scaffold_async_must_refuse_unknown_template()
    {
        using var workspace = new TempTestWorkspace();
        var scaffolder = new ProjectScaffolder(
            new ProjectTemplateCatalog(),
            workspaceRoot: workspace.Path);

        var result = await scaffolder.ScaffoldAsync(
            new Nebula.Core.Projects.ProjectScaffoldRequest(
                "no-such-template",
                workspace.Path));

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
        Assert.Empty(result.CreatedFiles);
    }

    [Fact]
    public async Task scaffold_async_must_refuse_target_outside_roots()
    {
        var scaffolder = new ProjectScaffolder(
            new ProjectTemplateCatalog(),
            workspaceRoot: @"C:\nebula-workspace",
            controlledTempRoot: @"C:\Temp\Nebula");

        var result = await scaffolder.ScaffoldAsync(
            new Nebula.Core.Projects.ProjectScaffoldRequest(
                "python-script",
                @"C:\Users\Public\evil"));

        Assert.False(result.Success);
        Assert.Contains("outside", result.Error);
    }

    [Fact]
    public async Task scaffold_async_must_allow_controlled_temp_root()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Nebula",
            "tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var scaffolder = new ProjectScaffolder(
                new ProjectTemplateCatalog(),
                workspaceRoot: @"C:\nebula-workspace",
                controlledTempRoot: tempRoot);

            var result = await scaffolder.ScaffoldAsync(
                new Nebula.Core.Projects.ProjectScaffoldRequest(
                    "python-script",
                    Path.Combine(tempRoot, "script")));

            Assert.True(result.Success);
            Assert.Contains(result.CreatedFiles, file => file == "main.py");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task scaffold_async_must_apply_project_name_placeholder()
    {
        using var workspace = new TempTestWorkspace();
        var scaffolder = new ProjectScaffolder(
            new ProjectTemplateCatalog(),
            workspaceRoot: workspace.Path);

        var result = await scaffolder.ScaffoldAsync(
            new Nebula.Core.Projects.ProjectScaffoldRequest(
                "python-package",
                workspace.Path,
                ProjectName: "MyProject"));

        Assert.True(result.Success);
    }
}
