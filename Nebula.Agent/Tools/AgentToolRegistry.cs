using Nebula.Core.Operations;

namespace Nebula.Agent.Tools;

public enum AgentToolExecutionKind
{
    Operation,
    FileEdit,
    Glob,
    Grep
}

public sealed record AgentToolParameterDefinition(
    string Name,
    string Description,
    bool Required);

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    OperationKind OperationKind,
    AgentToolExecutionKind ExecutionKind,
    IReadOnlyList<AgentToolParameterDefinition> Parameters,
    bool ReadOnly = false);

public interface IAgentToolRegistry
{
    IReadOnlyList<AgentToolDefinition> Definitions { get; }

    AgentToolDefinition? Find(string? name);
}

public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private static readonly AgentToolDefinition[] Tools =
    [
        new(
            "read",
            "Read a text file from the workspace or an allowed path.",
            OperationKind.FileRead,
            AgentToolExecutionKind.Operation,
            [
                new("targetPath", "Absolute file path.", true)
            ],
            ReadOnly: true),
        new(
            "write",
            "Create or overwrite a single text/data file with complete content.",
            OperationKind.FileWrite,
            AgentToolExecutionKind.Operation,
            [
                new("targetPath", "Absolute file path.", true),
                new("content", "Complete file content.", true)
            ]),
        new(
            "edit",
            "Replace an exact, unique string in an existing file.",
            OperationKind.FileWrite,
            AgentToolExecutionKind.FileEdit,
            [
                new("targetPath", "Absolute file path.", true),
                new("oldString", "Exact text to replace; must appear once.", true),
                new("newString", "Replacement text; empty deletes the match.", true)
            ]),
        new(
            "patch",
            "Create or overwrite multiple files in one planned patch.",
            OperationKind.PlannedPatch,
            AgentToolExecutionKind.Operation,
            [
                new("targetPath", "Root directory for relative paths.", true),
                new("plannedFiles", "Array of {path, content} objects.", true)
            ]),
        new(
            "shell",
            "Run a terminal command through the safety pipeline.",
            OperationKind.TerminalCommand,
            AgentToolExecutionKind.Operation,
            [
                new("command", "Command to run.", true)
            ]),
        new(
            "scaffold",
            "Create a project from a curated template.",
            OperationKind.ProjectScaffold,
            AgentToolExecutionKind.Operation,
            [
                new("templateId", "Template id from the catalog.", true),
                new("targetPath", "Project directory.", true)
            ]),
        new(
            "migration",
            "List, create or apply a database migration.",
            OperationKind.Migration,
            AgentToolExecutionKind.Operation,
            [
                new("migrationAction", "Create, Apply or List.", true)
            ]),
        new(
            "glob",
            "Find files by glob pattern inside the workspace.",
            OperationKind.FileRead,
            AgentToolExecutionKind.Glob,
            [
                new("pattern", "Glob pattern, e.g. src/**/*.cs.", true),
                new("targetPath", "Optional root directory; defaults to the workspace.", false),
                new("maxResults", "Optional result limit.", false)
            ],
            ReadOnly: true),
        new(
            "grep",
            "Search text files by regular expression inside the workspace.",
            OperationKind.FileRead,
            AgentToolExecutionKind.Grep,
            [
                new("pattern", "Regular expression.", true),
                new("include", "Optional glob filter, e.g. **/*.cs.", false),
                new("targetPath", "Optional root directory; defaults to the workspace.", false),
                new("maxResults", "Optional result limit.", false)
            ],
            ReadOnly: true)
    ];

    public IReadOnlyList<AgentToolDefinition> Definitions => Tools;

    public AgentToolDefinition? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
