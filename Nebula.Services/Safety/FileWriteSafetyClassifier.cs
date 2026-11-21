using Nebula.Core.Safety;

namespace Nebula.Services.Safety;

public sealed class FileWriteSafetyClassifier : IFileWriteSafetyClassifier
{
    private static readonly string[] AllowedExtensions =
        [".txt", ".md", ".json", ".cs", ".py", ".html", ".htm", ".css", ".js"];

    private static readonly string[] ApprovalExtensions =
        [".ps1", ".bat", ".cmd"];

    private static readonly string[] SensitiveSegments =
    [
        ".env", ".ssh", "id_rsa", "id_ed25519", "credentials",
        "access_token", "auth_token", "api_key", "apikey", "password"
    ];

    private readonly string defaultWorkspaceRoot;
    private readonly string defaultControlledTempRoot;

    public FileWriteSafetyClassifier(
        string? workspaceRoot = null,
        string? controlledTempRoot = null)
    {
        this.defaultWorkspaceRoot = Path.GetFullPath(
            workspaceRoot ?? Environment.CurrentDirectory);
        this.defaultControlledTempRoot = Path.GetFullPath(
            controlledTempRoot ?? Path.Combine(Path.GetTempPath(), "Nebula"));
    }

    public CommandClassification Classify(string targetPath, string? workspaceRoot = null, string? controlledTempRoot = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return Result(
                targetPath,
                CommandIntent.Blocked,
                1,
                "A file write requires a target path.");
        }

        var effectiveWorkspaceRoot = Path.GetFullPath(
            workspaceRoot ?? defaultWorkspaceRoot);
        var effectiveControlledTempRoot = Path.GetFullPath(
            controlledTempRoot ?? defaultControlledTempRoot);

        var fullPath = ResolvePath(targetPath, effectiveWorkspaceRoot);
        if (SensitiveSegments.Any(segment =>
            fullPath.Contains(
                segment,
                StringComparison.OrdinalIgnoreCase)))
        {
            return Result(
                fullPath,
                CommandIntent.DataExfiltration,
                0.99,
                "The write target is a credential, token, .env, or SSH file.");
        }

        if (!IsUnder(fullPath, effectiveWorkspaceRoot) &&
            !IsUnder(fullPath, effectiveControlledTempRoot))
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
                CommandIntent.NeedsApproval,
                0.99,
                $"File extension '{extension}' is not on the automatic write allowlist; " +
                "with an enabled sandbox it is created there, otherwise user approval is required.");
        }

        return Result(
            targetPath,
            CommandIntent.SafeWriteLocal,
            0.99,
            "The target is an allowed file inside the workspace or controlled temp directory.");
    }

    private static string ResolvePath(string path, string workspaceRoot)
    {
        var normalized = NormalizePathSeparators(path);
        return Path.IsPathRooted(normalized)
            ? Path.GetFullPath(normalized)
            : Path.GetFullPath(normalized, workspaceRoot);
    }

    private static string NormalizePathSeparators(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (path.Length >= 2 &&
            char.IsLetter(path[0]) &&
            path[1] == ':')
        {
            return path;
        }

        return Path.DirectorySeparatorChar == '/'
            ? path.Replace('\\', '/')
            : path.Replace('/', '\\');
    }

    private static bool IsUnder(string candidate, string root) =>
        PathContainment.IsUnder(candidate, root);

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
