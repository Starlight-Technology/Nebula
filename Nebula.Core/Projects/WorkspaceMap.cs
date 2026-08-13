using Nebula.Core.Agent;

namespace Nebula.Core.Projects;

public sealed record WorkspaceDependency(
    string Name,
    string? Version,
    string Kind);

public sealed record WorkspaceModule(
    string RelativePath,
    string? Kind);

public sealed record WorkspaceMap(
    string Root,
    WorkspaceStack Stack,
    IReadOnlyList<string> Files,
    IReadOnlyList<WorkspaceModule> Modules,
    IReadOnlyList<string> TestFiles,
    IReadOnlyList<WorkspaceDependency> Dependencies,
    IReadOnlyList<string> KnownCommands)
{
    public string BuildSummary(int maxFiles = 200)
    {
        var lines = new List<string> { $"Workspace root: {Root}" };
        lines.Add($"Detected stack: {Stack.Kind}");

        if (!string.IsNullOrWhiteSpace(Stack.ProjectFile))
        {
            lines.Add($"Project file: {Stack.ProjectFile}");
        }

        if (Stack.BuildCommand is not null)
        {
            lines.Add($"Build command: {Stack.BuildCommand}");
        }

        if (Stack.TestCommand is not null)
        {
            lines.Add($"Test command: {Stack.TestCommand}");
        }

        if (Stack.ParseCommand is not null)
        {
            lines.Add($"Parse command: {Stack.ParseCommand}");
        }

        if (Files.Count > 0)
        {
            var shown = Files.Take(maxFiles).ToList();
            var extra = Files.Count - shown.Count;
            lines.Add($"Files ({Files.Count}):");
            foreach (var file in shown)
            {
                lines.Add($"- {file}");
            }

            if (extra > 0)
            {
                lines.Add($"- ... and {extra} more");
            }
        }

        if (TestFiles.Count > 0)
        {
            lines.Add($"Test files ({TestFiles.Count}):");
            foreach (var testFile in TestFiles.Take(25))
            {
                lines.Add($"- {testFile}");
            }
        }

        if (Dependencies.Count > 0)
        {
            lines.Add($"Dependencies ({Dependencies.Count}):");
            foreach (var dependency in Dependencies.Take(30))
            {
                lines.Add($"- {dependency.Name} ({dependency.Kind}){DependencyVersion(dependency)}");
            }
        }

        if (KnownCommands.Count > 0)
        {
            lines.Add($"Known commands: {string.Join(" | ", KnownCommands)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DependencyVersion(WorkspaceDependency dependency) =>
        string.IsNullOrWhiteSpace(dependency.Version)
            ? string.Empty
            : $" {dependency.Version}";
}

public interface IWorkspaceMapService
{
    Task<WorkspaceMap> BuildAsync(string root, CancellationToken cancellationToken = default);
}
