namespace Nebula.Core.Safety;

/// <summary>
/// Autoridade unica para verificacao de contencao de paths no workspace.
/// Resolve symlinks (fail-closed: se o alvo real escapar da raiz, nao esta contido)
/// antes do check relativo <c>..</c>.
/// </summary>
public static class PathContainment
{
    private const int MaxSymlinkDepth = 8;

    public static bool IsUnder(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string fullCandidate;
        string fullRoot;
        try
        {
            fullCandidate = GetRealPath(candidate);
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception)
        {
            return false;
        }

        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        return relative != ".." &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    /// <summary>
    /// Resolve a cadeia de symlinks do path (e de seus ancestrais existentes).
    /// Nunca lanca: em caso de erro retorna <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    public static string GetRealPath(string path)
    {
        var current = Path.GetFullPath(path);
        try
        {
            for (var depth = 0; depth < MaxSymlinkDepth; depth++)
            {
                var probe = DeepestExistingAncestor(current);
                if (probe is null)
                {
                    return current;
                }

                FileSystemInfo info = Directory.Exists(probe) && !File.Exists(probe)
                    ? new DirectoryInfo(probe)
                    : new FileInfo(probe);
                var linkTarget = info.LinkTarget;
                if (string.IsNullOrEmpty(linkTarget))
                {
                    return current;
                }

                var resolvedBase = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(probe) ?? Path.GetPathRoot(probe) ?? "/", linkTarget));
                var remainder = Path.GetRelativePath(probe, current);
                current = remainder == "."
                    ? resolvedBase
                    : Path.GetFullPath(Path.Combine(resolvedBase, remainder));
            }

            return current;
        }
        catch (Exception)
        {
            return Path.GetFullPath(path);
        }
    }

    private static string? DeepestExistingAncestor(string path)
    {
        var probe = path;
        while (probe is not null && !File.Exists(probe) && !Directory.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }

        return probe;
    }
}
