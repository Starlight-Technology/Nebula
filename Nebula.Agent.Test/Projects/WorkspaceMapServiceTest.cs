using Nebula.Agent.Application;
using Nebula.Core.Agent;
using Nebula.Services.Projects;

namespace Nebula.Agent.Test.Projects;

public sealed class WorkspaceMapServiceTest
{
    [Fact]
    public async Task build_async_must_detect_dotnet_stack_files_modules_and_commands()
    {
        using var workspace = new TempTestWorkspace();
        WriteFile(workspace.Path, "src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        WriteFile(workspace.Path, "src/App/Program.cs", "Console.WriteLine(\"hi\");");
        WriteFile(workspace.Path, "tests/App.Tests/App.Tests.csproj", "<Project></Project>");
        WriteFile(workspace.Path, "tests/App.Tests/AppTests.cs", "public class AppTests {}");
        WriteFile(workspace.Path, "README.md", "# App");

        var service = new WorkspaceMapService(new DeterministicStackDetector());
        var map = await service.BuildAsync(workspace.Path);

        Assert.Equal(WorkspaceStackKind.DotNet, map.Stack.Kind);
        Assert.EndsWith("App.csproj", map.Stack.ProjectFile);
        Assert.Equal(5, map.Files.Count);
        Assert.Contains(map.Files, file => file == "src/App/Program.cs");
        Assert.Contains(map.Modules, module => module.RelativePath == "src/App/Program.cs" && module.Kind == "entrypoint");
        Assert.Contains(map.TestFiles, file => file == "tests/App.Tests/App.Tests.csproj");
        Assert.Contains(map.TestFiles, file => file == "tests/App.Tests/AppTests.cs");
        Assert.Contains(map.KnownCommands, command => command.StartsWith("dotnet build"));
        Assert.Contains(map.KnownCommands, command => command.StartsWith("dotnet test"));
    }

    [Fact]
    public async Task build_async_must_detect_node_dependencies_and_test_files()
    {
        using var workspace = new TempTestWorkspace();
        WriteFile(
            workspace.Path,
            "package.json",
            """
            {
              "name": "app",
              "scripts": { "test": "node --test" },
              "dependencies": { "express": "^4.19.0" },
              "devDependencies": { "typescript": "^5.0.0" }
            }
            """);
        WriteFile(workspace.Path, "index.js", "console.log('hi');");
        WriteFile(workspace.Path, "test/app.test.js", "test('x', () => {});");

        var service = new WorkspaceMapService(new DeterministicStackDetector());
        var map = await service.BuildAsync(workspace.Path);

        Assert.Equal(WorkspaceStackKind.Node, map.Stack.Kind);
        Assert.Contains(map.Dependencies, dependency => dependency.Name == "express" && dependency.Kind == "runtime");
        Assert.Contains(map.Dependencies, dependency => dependency.Name == "typescript" && dependency.Kind == "dev");
        Assert.Contains(map.TestFiles, file => file == "test/app.test.js");
        Assert.Contains(map.KnownCommands, command => command == "npm test");
    }

    [Fact]
    public async Task build_async_must_detect_python_stack_and_dependencies()
    {
        using var workspace = new TempTestWorkspace();
        WriteFile(workspace.Path, "pyproject.toml", "[project]\nname = \"pkg\"\n\"requests>=2.0\"\n");
        WriteFile(workspace.Path, "src/package/__init__.py", "");
        WriteFile(workspace.Path, "tests/test_core.py", "def test_x(): pass");

        var service = new WorkspaceMapService(new DeterministicStackDetector());
        var map = await service.BuildAsync(workspace.Path);

        Assert.Equal(WorkspaceStackKind.Python, map.Stack.Kind);
        Assert.Contains(map.Dependencies, dependency => dependency.Name == "requests");
        Assert.Contains(map.TestFiles, file => file == "tests/test_core.py");
        Assert.Contains(map.KnownCommands, command => command == "python -m pytest");
    }

    [Fact]
    public async Task build_async_must_ignore_bin_obj_and_node_modules()
    {
        using var workspace = new TempTestWorkspace();
        WriteFile(workspace.Path, "src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        WriteFile(workspace.Path, "src/App/Program.cs", "Console.WriteLine(\"hi\");");
        WriteFile(workspace.Path, "src/App/bin/Release/output.dll", "binary");
        WriteFile(workspace.Path, "src/App/obj/Debug/App.csproj.nuget.g.props", "props");
        WriteFile(workspace.Path, "node_modules/pkg/index.js", "x");

        var service = new WorkspaceMapService(new DeterministicStackDetector());
        var map = await service.BuildAsync(workspace.Path);

        Assert.DoesNotContain(map.Files, file => file.Contains("/bin/"));
        Assert.DoesNotContain(map.Files, file => file.Contains("/obj/"));
        Assert.DoesNotContain(map.Files, file => file.Contains("node_modules"));
    }

    [Fact]
    public async Task build_summary_must_include_stack_commands_and_file_count()
    {
        using var workspace = new TempTestWorkspace();
        WriteFile(workspace.Path, "main.py", "print('hi')");

        var service = new WorkspaceMapService(new DeterministicStackDetector());
        var map = await service.BuildAsync(workspace.Path);
        var summary = map.BuildSummary();

        Assert.Contains("Detected stack: Python", summary);
        Assert.Contains("main.py", summary);
        Assert.Contains("python -m pytest", summary);
    }

    [Fact]
    public async Task build_async_must_return_empty_map_for_missing_directory()
    {
        var service = new WorkspaceMapService(new DeterministicStackDetector());
        var map = await service.BuildAsync(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nebula-missing", Guid.NewGuid().ToString("N")));

        Assert.Equal(WorkspaceStackKind.Unknown, map.Stack.Kind);
        Assert.Empty(map.Files);
        Assert.Empty(map.Dependencies);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var fullPath = System.IO.Path.Combine(root, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
