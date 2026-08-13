using System.Text.Json;

using Nebula.Core.Agent;
using Nebula.Core.Projects;

namespace Nebula.Services.Projects;

public sealed class WorkspaceMapService : IWorkspaceMapService
{
    private static readonly string[] IgnoredDirectories =
    [
        "bin",
        "obj",
        "node_modules",
        ".git",
        ".vs",
        ".idea",
        "dist",
        "build",
        "packages",
        "artifacts"
    ];

    private static readonly string[] DependencyFileNames =
    [
        "package.json",
        "requirements.txt",
        "Pipfile",
        "pyproject.toml"
    ];

    private const int MaxSearchDepth = 5;
    private const int MaxFiles = 1000;

    private readonly IWorkspaceStackDetector stackDetector;

    public WorkspaceMapService(IWorkspaceStackDetector stackDetector)
    {
        this.stackDetector = stackDetector;
    }

    public Task<WorkspaceMap> BuildAsync(string root, CancellationToken cancellationToken = default)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            return Task.FromResult(new WorkspaceMap(
                fullRoot,
                new WorkspaceStack(WorkspaceStackKind.Unknown, null, null, null, null),
                [],
                [],
                [],
                [],
                []));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var files = SafeEnumerateFiles(fullRoot, MaxSearchDepth)
            .Take(MaxFiles)
            .ToList();
        var relativeFiles = files
            .Select(path => ToRelative(fullRoot, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stack = stackDetector.Detect(fullRoot);
        var modules = DetectModules(relativeFiles, stack.Kind);
        var testFiles = relativeFiles
            .Where(IsTestFile)
            .ToList();
        var dependencies = DetectDependencies(fullRoot, relativeFiles);
        var knownCommands = BuildKnownCommands(stack);

        return Task.FromResult(new WorkspaceMap(
            fullRoot,
            stack,
            relativeFiles,
            modules,
            testFiles,
            dependencies,
            knownCommands));
    }

    private static string ToRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static IReadOnlyList<WorkspaceModule> DetectModules(
        IReadOnlyList<string> files,
        WorkspaceStackKind stack)
    {
        var modules = new List<WorkspaceModule>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var kind = name switch
            {
                "Program.cs" or "Main.cs" => "entrypoint",
                "Program.fs" => "entrypoint",
                "index.js" or "index.ts" or "main.js" or "cli.js" => "entrypoint",
                "main.py" or "__main__.py" => "entrypoint",
                _ when name.EndsWith("Tests.csproj", StringComparison.OrdinalIgnoreCase) => "test-project",
                _ when name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) => "test-module",
                _ when name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase) => "test-module",
                _ when name.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase) => "test-module",
                _ when name.EndsWith(".spec.js", StringComparison.OrdinalIgnoreCase) => "test-module",
                _ => null
            };

            if (kind is not null)
            {
                modules.Add(new WorkspaceModule(file, kind));
            }
        }

        return modules;
    }

    private static bool IsTestFile(string file)
    {
        var name = Path.GetFileName(file);
        return name.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Test.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Tests.csproj", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".spec.js", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<WorkspaceDependency> DetectDependencies(
        string root,
        IReadOnlyList<string> files)
    {
        var dependencies = new List<WorkspaceDependency>();

        foreach (var fileName in DependencyFileNames)
        {
            var path = files.FirstOrDefault(
                file => string.Equals(
                    Path.GetFileName(file),
                    fileName,
                    StringComparison.OrdinalIgnoreCase));
            if (path is null)
            {
                continue;
            }

            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            switch (fileName)
            {
                case "package.json":
                    dependencies.AddRange(ReadPackageJsonDependencies(fullPath));
                    break;
                case "requirements.txt":
                    dependencies.AddRange(ReadRequirementsTxt(fullPath));
                    break;
                case "pyproject.toml":
                    dependencies.AddRange(ReadPyProjectDependencies(fullPath));
                    break;
            }
        }

        return dependencies
            .GroupBy(dependency => $"{dependency.Name}|{dependency.Kind}")
            .Select(group => group.First())
            .OrderBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<WorkspaceDependency> ReadPackageJsonDependencies(string path)
    {
        var result = new List<WorkspaceDependency>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var propertyName in new[] { "dependencies", "devDependencies" })
            {
                if (!document.RootElement.TryGetProperty(propertyName, out var section))
                {
                    continue;
                }

                foreach (var property in section.EnumerateObject())
                {
                    result.Add(new WorkspaceDependency(
                        property.Name,
                        property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString()
                            : null,
                        propertyName == "devDependencies" ? "dev" : "runtime"));
                }
            }
        }
        catch (Exception)
        {
            // Unreadable manifests are not fatal for the workspace map.
        }

        return result;
    }

    private static IReadOnlyList<WorkspaceDependency> ReadRequirementsTxt(string path)
    {
        var result = new List<WorkspaceDependency>();
        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var parts = line.Split(
                    new[] { "==", ">=", "<=", "~=" },
                    StringSplitOptions.None);
                result.Add(new WorkspaceDependency(
                    parts[0].Trim(),
                    parts.Length > 1 ? parts[1].Trim() : null,
                    "runtime"));
            }
        }
        catch (Exception)
        {
            // Unreadable manifests are not fatal for the workspace map.
        }

        return result;
    }

    private static IReadOnlyList<WorkspaceDependency> ReadPyProjectDependencies(string path)
    {
        var result = new List<WorkspaceDependency>();
        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("\"") || !line.EndsWith("\""))
                {
                    continue;
                }

                var inner = line.Trim('"');
                if (inner.Length == 0 || inner.StartsWith('#'))
                {
                    continue;
                }

                var parts = inner.Split(
                    new[] { "==", ">=", "<=", "~=" },
                    StringSplitOptions.None);
                result.Add(new WorkspaceDependency(
                    parts[0].Trim(),
                    parts.Length > 1 ? parts[1].Trim() : null,
                    "runtime"));
            }
        }
        catch (Exception)
        {
            // Unreadable manifests are not fatal for the workspace map.
        }

        return result;
    }

    private static IReadOnlyList<string> BuildKnownCommands(WorkspaceStack stack)
    {
        var commands = new List<string>();
        if (!string.IsNullOrWhiteSpace(stack.BuildCommand))
        {
            commands.Add(stack.BuildCommand);
        }

        if (!string.IsNullOrWhiteSpace(stack.TestCommand))
        {
            commands.Add(stack.TestCommand);
        }

        if (!string.IsNullOrWhiteSpace(stack.ParseCommand))
        {
            commands.Add(stack.ParseCommand);
        }

        switch (stack.Kind)
        {
            case WorkspaceStackKind.DotNet:
                if (!commands.Contains("dotnet build"))
                {
                    commands.Add("dotnet build");
                }

                if (!commands.Contains("dotnet test"))
                {
                    commands.Add("dotnet test");
                }

                break;
            case WorkspaceStackKind.Node:
                if (!commands.Contains("npm test"))
                {
                    commands.Add("npm test");
                }

                break;
            case WorkspaceStackKind.Python:
                if (!commands.Contains("python -m pytest"))
                {
                    commands.Add("python -m pytest");
                }

                break;
        }

        return commands;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, int maxDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth > maxDepth)
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (Exception)
            {
                files = [];
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth < maxDepth)
            {
                string[] directories;
                try
                {
                    directories = Directory.GetDirectories(current);
                }
                catch (Exception)
                {
                    directories = [];
                }

                foreach (var directory in directories)
                {
                    var name = Path.GetFileName(directory);
                    if (!IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        queue.Enqueue((directory, depth + 1));
                    }
                }
            }
        }
    }
}
