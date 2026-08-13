using System.Text.Json;

using Nebula.Core.Agent;

namespace Nebula.Agent.Application;

public sealed class DeterministicStackDetector : IWorkspaceStackDetector
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

    private const int MaxSearchDepth = 3;

    public WorkspaceStack Detect(string workingDirectory)
    {
        var root = NormalizeRoot(workingDirectory);

        var dotNetProject = FindDotNetProject(root);
        if (dotNetProject is not null)
        {
            return new WorkspaceStack(
                WorkspaceStackKind.DotNet,
                dotNetProject,
                BuildCommand: $"dotnet build \"{dotNetProject}\"",
                TestCommand: $"dotnet test \"{dotNetProject}\"",
                ParseCommand: null,
                LintCommand: "dotnet format --verify-no-changes --no-restore");
        }

        var packageJson = FindFile(root, "package.json");
        if (packageJson is not null)
        {
            var scripts = TryReadScripts(packageJson);
            var testCommand = scripts.TryGetValue("test", out var test) && !string.IsNullOrWhiteSpace(test)
                ? "npm test"
                : null;
            var buildCommand = scripts.TryGetValue("build", out var build) && !string.IsNullOrWhiteSpace(build)
                ? "npm run build"
                : null;
            var lintCommand = scripts.TryGetValue("lint", out var lint) && !string.IsNullOrWhiteSpace(lint)
                ? "npm run lint"
                : null;
            if (testCommand is not null || buildCommand is not null || lintCommand is not null)
            {
                return new WorkspaceStack(
                    WorkspaceStackKind.Node,
                    packageJson,
                    BuildCommand: buildCommand,
                    TestCommand: testCommand,
                    ParseCommand: null,
                    LintCommand: lintCommand);
            }
        }

        var pythonMarker = FindFile(root, "pyproject.toml")
            ?? FindFile(root, "requirements.txt")
            ?? FindFile(root, "Pipfile");
        if (pythonMarker is not null || ContainsPythonSource(root))
        {
            var pyFiles = FindPythonSources(root);
            var parseCommand = pyFiles.Count == 0
                ? null
                : "python -m py_compile " + string.Join(" ", pyFiles
                    .Take(10)
                    .Select(path => $"\"{path}\""));
            return new WorkspaceStack(
                WorkspaceStackKind.Python,
                pythonMarker,
                BuildCommand: null,
                TestCommand: "python -m pytest",
                ParseCommand: parseCommand);
        }

        return new WorkspaceStack(
            WorkspaceStackKind.Unknown,
            null,
            BuildCommand: null,
            TestCommand: null,
            ParseCommand: null);
    }

    private static string NormalizeRoot(string workingDirectory)
    {
        var fullPath = Path.GetFullPath(workingDirectory);
        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        return Directory.Exists(fullPath)
            ? fullPath
            : Environment.CurrentDirectory;
    }

    private static string? FindDotNetProject(string root)
    {
        var solution = FindFile(root, "*.sln");
        if (solution is not null)
        {
            return solution;
        }

        return FindFile(root, "*.csproj");
    }

    private static string? FindFile(string root, string pattern)
    {
        if (File.Exists(Path.Combine(root, pattern)) && !pattern.Contains('*'))
        {
            return Path.Combine(root, pattern);
        }

        var files = SafeEnumerateFiles(root, pattern, MaxSearchDepth)
            .OrderBy(path => path.Length)
            .ToList();
        if (files.Count > 0)
        {
            return files[0];
        }

        return null;
    }

    private static List<string> FindPythonSources(string root)
    {
        return SafeEnumerateFiles(root, "*.py", MaxSearchDepth)
            .OrderBy(path => path.Length)
            .ToList();
    }

    private static bool ContainsPythonSource(string root)
    {
        return SafeEnumerateFiles(root, "*.py", 1)
            .Take(1)
            .Any();
    }

    private static IEnumerable<string> SafeEnumerateFiles(
        string root,
        string pattern,
        int maxDepth)
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
                files = Directory.GetFiles(current, pattern);
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

    private static Dictionary<string, string> TryReadScripts(string packageJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts))
            {
                return [];
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in scripts.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return result;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
