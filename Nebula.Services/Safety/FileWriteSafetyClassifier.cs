using Nebula.Core.Safety;

namespace Nebula.Services.Safety;

public sealed class FileWriteSafetyClassifier : IFileWriteSafetyClassifier
{
    private static readonly string[] AllowedExtensions =
        [".txt", ".md", ".json", ".cs", ".py"];

    private static readonly string[] ApprovalExtensions =
        [".ps1", ".bat", ".cmd"];

    private readonly string workspaceRoot;
    private readonly string controlledTempRoot;

    public FileWriteSafetyClassifier(
        string? workspaceRoot = null,
        string? controlledTempRoot = null)
    {
        this.workspaceRoot = Path.GetFullPath(
            workspaceRoot ?? Environment.CurrentDirectory);
        this.controlledTempRoot = Path.GetFullPath(
            controlledTempRoot ?? Path.Combine(Path.GetTempPath(), "Nebula"));
    }

    public CommandClassification Classify(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return Result(
                targetPath,
                CommandIntent.Blocked,
                1,
                "A file write requires a target path.");
        }

        var fullPath = ResolvePath(targetPath);
        if (!IsUnder(fullPath, workspaceRoot) &&
            !IsUnder(fullPath, controlledTempRoot))
        {
            return Result(
                targetPath,
                CommandIntent.NeedsApproval,
                0.99,
                $"The write target is outside the workspace or controlled temp directory: {fullPath}");
        }

        var extension = Path.GetExtension(fullPath);
        if (ApprovalExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Result(
                targetPath,
                CommandIntent.NeedsApproval,
                0.99,
                $"Executable script extension '{extension}' requires approval.");
        }

        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Result(
                targetPath,
                CommandIntent.Blocked,
                0.99,
                $"File extension '{extension}' is not on the write allowlist.");
        }

        return Result(
            targetPath,
            CommandIntent.SafeWriteLocal,
            0.99,
            "The target is an allowed file inside the workspace or controlled temp directory.");
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, workspaceRoot);

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
            nameof(FileWriteSafetyClassifier),
            [reason]);
}
