using System.Text;

using Nebula.Core.Projects;

namespace Nebula.Services.Projects;

public sealed class ProjectScaffolder : IProjectScaffolder
{
    private readonly IProjectTemplateCatalog catalog;
    private readonly string workspaceRoot;
    private readonly string controlledTempRoot;

    public ProjectScaffolder(
        IProjectTemplateCatalog catalog,
        string? workspaceRoot = null,
        string? controlledTempRoot = null)
    {
        this.catalog = catalog;
        this.workspaceRoot = Path.GetFullPath(
            workspaceRoot ?? Environment.CurrentDirectory);
        this.controlledTempRoot = Path.GetFullPath(
            controlledTempRoot ?? Path.Combine(Path.GetTempPath(), "Nebula"));
    }

    public async Task<ProjectScaffoldResult> ScaffoldAsync(
        ProjectScaffoldRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TemplateId);

        var template = catalog.FindById(request.TemplateId);
        if (template is null)
        {
            return ProjectScaffoldResult.Failed(
                request.TemplateId,
                $"Template '{request.TemplateId}' was not found in the catalog.");
        }

        var targetDirectory = NormalizeTarget(request.TargetDirectory);
        if (!IsUnder(targetDirectory, workspaceRoot) &&
            !IsUnder(targetDirectory, controlledTempRoot))
        {
            return ProjectScaffoldResult.Failed(
                request.TemplateId,
                $"Scaffold target is outside the workspace or controlled temp directory: {targetDirectory}");
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);

            var createdFiles = new List<string>();
            foreach (var file in template.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fullPath = Path.GetFullPath(
                    Path.Combine(targetDirectory, file.RelativePath));
                if (!IsUnder(fullPath, targetDirectory))
                {
                    return ProjectScaffoldResult.Failed(
                        request.TemplateId,
                        $"Template file escapes the target directory: {file.RelativePath}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                var content = ApplyPlaceholders(file.Content, request.ProjectName);
                await File.WriteAllTextAsync(
                    fullPath,
                    content,
                    Encoding.UTF8,
                    cancellationToken);
                createdFiles.Add(file.RelativePath.Replace('\\', '/'));
            }

            return new ProjectScaffoldResult(
                Success: true,
                Error: null,
                template.Id,
                createdFiles,
                template.SetupCommands,
                template.VerificationCommands);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProjectScaffoldResult.Failed(
                request.TemplateId,
                $"Scaffold failed: {ex.Message}");
        }
    }

    private static string ApplyPlaceholders(string content, string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return content;
        }

        return content.Replace("{{PROJECT_NAME}}", projectName);
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
}
