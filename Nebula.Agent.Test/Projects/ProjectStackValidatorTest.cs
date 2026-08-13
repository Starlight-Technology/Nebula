using Nebula.Agent.Application;
using Nebula.Core.Agent;
using Nebula.Services.Projects;

namespace Nebula.Agent.Test.Projects;

public sealed class ProjectStackValidatorTest
{
    [Fact]
    public async Task validate_async_must_pass_when_essential_files_exist()
    {
        using var workspace = new TempTestWorkspace();
        WriteFile(workspace.Path, "main.py", "print('hi')");
        WriteFile(workspace.Path, "README.md", "# script");

        var catalog = new ProjectTemplateCatalog();
        var validator = new ProjectStackValidator(
            catalog,
            new WorkspaceMapService(new DeterministicStackDetector()));

        var result = await validator.ValidateAsync(workspace.Path, "python-script");

        Assert.True(result.Success);
        Assert.Equal(WorkspaceStackKind.Python, result.Stack);
        Assert.Contains("main.py", result.PresentEssentialFiles);
        Assert.Empty(result.MissingEssentialFiles);
        Assert.NotEmpty(result.SuggestedCommands);
    }

    [Fact]
    public async Task validate_async_must_report_missing_essential_files()
    {
        using var workspace = new TempTestWorkspace();
        WriteFile(workspace.Path, "README.md", "# empty");

        var validator = new ProjectStackValidator(
            new ProjectTemplateCatalog(),
            new WorkspaceMapService(new DeterministicStackDetector()));

        var result = await validator.ValidateAsync(workspace.Path, "python-script");

        Assert.False(result.Success);
        Assert.Contains("main.py", result.MissingEssentialFiles);
    }

    [Fact]
    public async Task validate_async_must_report_unknown_template()
    {
        using var workspace = new TempTestWorkspace();

        var validator = new ProjectStackValidator(
            new ProjectTemplateCatalog(),
            new WorkspaceMapService(new DeterministicStackDetector()));

        var result = await validator.ValidateAsync(workspace.Path, "missing-template");

        Assert.False(result.Success);
        Assert.Contains("template:missing-template", result.MissingEssentialFiles);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
