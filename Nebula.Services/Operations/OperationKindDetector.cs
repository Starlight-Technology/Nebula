using System.Text.RegularExpressions;

using Nebula.Core.Operations;

namespace Nebula.Services.Operations;

public sealed class OperationKindDetector : IOperationKindDetector
{
    public OperationKind Detect(AgentStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (step.DeclaredKind != OperationKind.Unknown)
        {
            return step.DeclaredKind;
        }

        var original = step.OriginalText.Trim();
        var command = step.Command?.Trim() ?? string.Empty;
        var content = step.Content?.Trim() ?? string.Empty;
        var targetPath = step.TargetPath?.Trim() ?? string.Empty;

        if (LearningRegex.IsMatch(original))
        {
            return OperationKind.Learning;
        }

        if (ResearchRegex.IsMatch(original))
        {
            return OperationKind.Research;
        }

        if (ProjectScaffoldRegex.IsMatch(original) ||
            ProjectScaffoldRegex.IsMatch(command))
        {
            return OperationKind.ProjectScaffold;
        }

        if (PlannedPatchRegex.IsMatch(original) ||
            PlannedPatchRegex.IsMatch(command))
        {
            return OperationKind.PlannedPatch;
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            return IsScriptPath(targetPath) || LooksLikeScript(content)
                ? OperationKind.ScriptContent
                : OperationKind.FileWrite;
        }

        if (LooksLikeScript(command) && !LooksLikeTerminalInvocation(command))
        {
            return OperationKind.ScriptContent;
        }

        if (ScriptExecutionRegex.IsMatch(command))
        {
            return OperationKind.ScriptExecution;
        }

        if (!string.IsNullOrWhiteSpace(targetPath) &&
            (FileReadRegex.IsMatch(command) || FileReadRegex.IsMatch(original)))
        {
            return OperationKind.FileRead;
        }

        return string.IsNullOrWhiteSpace(command)
            ? OperationKind.Unknown
            : OperationKind.TerminalCommand;
    }

    private static bool IsScriptPath(string path) =>
        new[] { ".py", ".cs", ".ps1", ".bat", ".cmd" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool LooksLikeScript(string value) =>
        ScriptContentRegex.IsMatch(value);

    private static bool LooksLikeTerminalInvocation(string value) =>
        TerminalInvocationRegex.IsMatch(value);

    private static readonly Regex LearningRegex = new(
        @"\b(?:aprenda|aprender|estude|estudar|learn)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ResearchRegex = new(
        @"\b(?:pesquise|pesquisar|investigue|research|search)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProjectScaffoldRegex = new(
        @"\bscaffold\b|\b(?:crie|criar|creat(?:e|es|ed|ing)|inicialize|init)\b.{0,40}\b(?:projeto|project)\b.{0,30}\b(?:dotnet|\.net|c#|csharp|python|node|nodejs|javascript|api|console|cli|package|pacote|react|blazor|worker|template)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlannedPatchRegex = new(
        @"\bpatch\b|\b(?:altere|altera|mude|muda|modifique|modifica|edite|edita)\b.{0,50}\b(?:arquivos?|files?|varios arquivos|vários arquivos)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScriptExecutionRegex = new(
        @"^\s*(?:(?:python|python3|py)\s+.+\.py|dotnet\s+(?:run|build|test)\b.*(?:\.csproj|\.sln)?|(?:powershell|pwsh).+\.ps1|.+\.(?:ps1|bat|cmd))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FileReadRegex = new(
        @"(?:^|\s)(?:cat|type|get-content|read|ler|leia)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScriptContentRegex = new(
        @"(?:^|\n)\s*(?:import\s+\w+|using\s+[\w.]+;|namespace\s+\w+|class\s+\w+|def\s+\w+|print\s*\(|console\.writeline\s*\(|json\.dumps\s*\(|\{[\s\S]*[""']\w+[""']\s*:)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TerminalInvocationRegex = new(
        @"^\s*(?:echo|printf|dir|ls|pwd|cd|dotnet|python|python3|py|powershell|pwsh|cmd|bash|sh|git|docker|mkdir|new-item|get-childitem)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
