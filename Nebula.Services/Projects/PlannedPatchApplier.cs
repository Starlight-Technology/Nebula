using Nebula.Core.Projects;

namespace Nebula.Services.Projects;

public sealed class PlannedPatchApplier : IPlannedPatchApplier
{
    private readonly string workspaceRoot;
    private readonly string controlledTempRoot;

    public PlannedPatchApplier(
        string? workspaceRoot = null,
        string? controlledTempRoot = null)
    {
        this.workspaceRoot = Path.GetFullPath(
            workspaceRoot ?? Environment.CurrentDirectory);
        this.controlledTempRoot = Path.GetFullPath(
            controlledTempRoot ?? Path.Combine(Path.GetTempPath(), "Nebula"));
    }

    public async Task<PlannedPatchResult> ApplyAsync(
        PlannedPatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Files.Count == 0)
        {
            return PlannedPatchResult.Failed(
                "A planned patch requires at least one file.");
        }

        var targetDirectory = NormalizeTarget(request.TargetDirectory);
        if (!IsUnder(targetDirectory, workspaceRoot) &&
            !IsUnder(targetDirectory, controlledTempRoot))
        {
            return PlannedPatchResult.Failed(
                $"The patch target directory is outside the workspace or " +
                $"controlled temp directory: {targetDirectory}");
        }

        var appliedFiles = new List<string>();
        foreach (var file in request.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(file.RelativePath) ||
                Path.IsPathRooted(file.RelativePath))
            {
                return PlannedPatchResult.Failed(
                    $"Patch file '{file.RelativePath}' is not a valid relative path.");
            }

            var fullPath = NormalizeTarget(
                Path.Combine(targetDirectory, file.RelativePath));
            if (!IsUnder(fullPath, targetDirectory))
            {
                return PlannedPatchResult.Failed(
                    $"Patch file '{file.RelativePath}' escapes the target directory.");
            }

            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await File.WriteAllTextAsync(
                fullPath,
                file.Content,
                System.Text.Encoding.UTF8,
                cancellationToken);
            appliedFiles.Add(
                Path.GetRelativePath(targetDirectory, fullPath)
                    .Replace(Path.DirectorySeparatorChar, '/'));
        }

        return new PlannedPatchResult(
            true,
            null,
            appliedFiles);
    }

    private static string NormalizeTarget(string path)
    {
        try
        {
            return Path.GetFullPath(path);
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
}
