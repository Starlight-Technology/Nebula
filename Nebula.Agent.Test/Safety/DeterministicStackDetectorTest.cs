using Nebula.Agent.Application;
using Nebula.Core.Agent;

namespace Nebula.Agent.Test.Safety;

public sealed class DeterministicStackDetectorTest
{
    [Fact]
    public void detect_must_find_dotnet_solution()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "App.sln"), "");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.DotNet, stack.Kind);
        Assert.EndsWith("App.sln", stack.ProjectFile);
        Assert.Contains("dotnet build", stack.BuildCommand);
        Assert.Contains("dotnet test", stack.TestCommand);
        Assert.Contains("App.sln", stack.BuildCommand);
    }

    [Fact]
    public void detect_must_find_dotnet_project_when_no_solution_exists()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "App.csproj"), "");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.DotNet, stack.Kind);
        Assert.EndsWith("App.csproj", stack.ProjectFile);
    }

    [Fact]
    public void detect_must_ignore_bin_and_obj_directories()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "bin"));
        Directory.CreateDirectory(Path.Combine(directory.Path, "obj"));
        File.WriteAllText(Path.Combine(directory.Path, "bin", "old.csproj"), "");
        File.WriteAllText(Path.Combine(directory.Path, "obj", "old.csproj"), "");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.Unknown, stack.Kind);
    }

    [Fact]
    public void detect_must_find_node_project_with_test_script()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "package.json"),
            """{"name":"app","scripts":{"test":"vitest run","build":"tsc"}}""");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.Node, stack.Kind);
        Assert.Equal("npm test", stack.TestCommand);
        Assert.Equal("npm run build", stack.BuildCommand);
    }

    [Fact]
    public void detect_must_detect_node_lint_script()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "package.json"),
            """{"name":"app","scripts":{"lint":"eslint ."}}""");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.Node, stack.Kind);
        Assert.Equal("npm run lint", stack.LintCommand);
        Assert.Null(stack.TestCommand);
        Assert.Null(stack.BuildCommand);
    }

    [Fact]
    public void detect_must_set_dotnet_format_as_lint_command()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "App.csproj"), "");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.DotNet, stack.Kind);
        Assert.Equal("dotnet format --verify-no-changes --no-restore", stack.LintCommand);
    }

    [Fact]
    public void detect_must_not_set_lint_command_for_python()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "app.py"), "print('hello')");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.Python, stack.Kind);
        Assert.Null(stack.LintCommand);
    }

    [Fact]
    public void detect_must_find_node_project_without_test_script()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "package.json"),
            """{"name":"app","scripts":{"build":"tsc"}}""");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.Node, stack.Kind);
        Assert.Null(stack.TestCommand);
        Assert.Equal("npm run build", stack.BuildCommand);
    }

    [Fact]
    public void detect_must_ignore_node_project_without_verifiable_scripts()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "package.json"),
            """{"name":"app","scripts":{"start":"node index.js"}}""");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.Unknown, stack.Kind);
    }

    [Fact]
    public void detect_must_find_python_project_with_pyproject()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "pyproject.toml"), "");
        File.WriteAllText(Path.Combine(directory.Path, "app.py"), "print('hello')");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.Python, stack.Kind);
        Assert.Equal("python -m pytest", stack.TestCommand);
        Assert.NotNull(stack.ParseCommand);
        Assert.Contains("py_compile", stack.ParseCommand);
        Assert.Contains("app.py", stack.ParseCommand);
    }

    [Fact]
    public void detect_must_find_python_project_by_source_file()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "main.py"), "print('hello')");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.Python, stack.Kind);
    }

    [Fact]
    public void detect_must_return_unknown_for_empty_directory()
    {
        using var directory = new TempDirectory();

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.Unknown, stack.Kind);
        Assert.Null(stack.BuildCommand);
        Assert.Null(stack.TestCommand);
    }

    [Fact]
    public void detect_must_find_project_in_nested_directory()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "src"));
        File.WriteAllText(Path.Combine(directory.Path, "src", "App.csproj"), "");

        var stack = new DeterministicStackDetector().Detect(directory.Path);

        Assert.Equal(WorkspaceStackKind.DotNet, stack.Kind);
        Assert.Contains("src", stack.ProjectFile);
    }

    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
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
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                // best effort cleanup
            }
        }
    }
}
