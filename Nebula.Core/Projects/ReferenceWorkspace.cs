namespace Nebula.Core.Projects;

/// <summary>
/// The project folder Nebula is working on. A reference workspace always exists
/// on disk: an explicitly requested path is created when missing (even when the
/// folder would be empty), and when nothing is specified a fresh empty workspace
/// is created under the system temp directory.
/// </summary>
public sealed record ReferenceWorkspace(
    string Root,
    string DisplayName,
    bool IsNew,
    bool IsEmpty)
{
    public const string DefaultWorkspaceFolderName = "nebula-workspace";

    /// <summary>
    /// Resolves the effective workspace root. When <paramref name="requestedRoot"/>
    /// is null or whitespace, a dedicated default workspace under the temp
    /// directory is used instead of the process current directory.
    /// </summary>
    public static ReferenceWorkspace Resolve(string? requestedRoot)
    {
        var root = string.IsNullOrWhiteSpace(requestedRoot)
            ? Path.Combine(Path.GetTempPath(), DefaultWorkspaceFolderName)
            : Path.GetFullPath(requestedRoot);

        var wasCreated = !Directory.Exists(root);
        Directory.CreateDirectory(root);

        var isEmpty = !SafeEnumerateFileSystemEntries(root).Any();
        return new ReferenceWorkspace(
            root,
            Path.GetFileName(root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)),
            wasCreated,
            isEmpty);
    }

    private static IEnumerable<string> SafeEnumerateFileSystemEntries(string root)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(root);
        }
        catch (Exception)
        {
            return [];
        }
    }
}
