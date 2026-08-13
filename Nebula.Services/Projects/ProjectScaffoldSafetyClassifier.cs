using Nebula.Core.Safety;

namespace Nebula.Services.Projects;

public sealed class ProjectScaffoldSafetyClassifier
{
    private readonly string workspaceRoot;
    private readonly string controlledTempRoot;

    public ProjectScaffoldSafetyClassifier(
        string? workspaceRoot = null,
        string? controlledTempRoot = null)
    {
        this.workspaceRoot = Path.GetFullPath(
            workspaceRoot ?? Environment.CurrentDirectory);
        this.controlledTempRoot = Path.GetFullPath(
            controlledTempRoot ?? Path.Combine(Path.GetTempPath(), "Nebula"));
    }

    public CommandClassification Classify(string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return Result(
                targetDirectory,
                CommandIntent.Blocked,
                1,
                "A scaffold requires a target directory.");
        }

        var fullPath = NormalizeTarget(targetDirectory);
        if (!IsUnder(fullPath, workspaceRoot) &&
            !IsUnder(fullPath, controlledTempRoot))
        {
            return Result(
                targetDirectory,
                CommandIntent.NeedsApproval,
                0.99,
                $"The scaffold target is outside the workspace or controlled temp directory: {fullPath}");
        }

        return Result(
            targetDirectory,
            CommandIntent.SafeWriteLocal,
            0.99,
            "The scaffold target is inside the workspace or controlled temp directory. Template content is curated and deterministic.");
    }

    private static string NormalizeTarget(string targetDirectory)
    {
        try
        {
            return Path.GetFullPath(targetDirectory);
        }
        catch (Exception)
        {
            return Environment.CurrentDirectory;
        }
    }

    private static bool IsUnder(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static CommandClassification Result(
        string text,
        CommandIntent intent,
        double confidence,
        string reason) =>
        new(
            text,
            intent,
            confidence,
            nameof(ProjectScaffoldSafetyClassifier),
            [reason]);
}
