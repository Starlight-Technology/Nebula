using Nebula.Core.Projects;

namespace Nebula.Services.Projects;

public sealed class ProjectStackValidator : IProjectStackValidator
{
    private readonly IProjectTemplateCatalog catalog;
    private readonly IWorkspaceMapService workspaceMapService;

    public ProjectStackValidator(
        IProjectTemplateCatalog catalog,
        IWorkspaceMapService workspaceMapService)
    {
        this.catalog = catalog;
        this.workspaceMapService = workspaceMapService;
    }

    public async Task<ProjectValidationResult> ValidateAsync(
        string root,
        string templateId,
        CancellationToken cancellationToken = default)
    {
        var template = catalog.FindById(templateId);
        if (template is null)
        {
            return ProjectValidationResult.Invalid(
                root,
                Nebula.Core.Agent.WorkspaceStackKind.Unknown,
                [],
                [$"template:{templateId}"],
                []);
        }

        var map = await workspaceMapService.BuildAsync(root, cancellationToken);
        var present = new List<string>();
        var missing = new List<string>();

        foreach (var essential in template.EssentialFiles)
        {
            var normalized = essential.Replace('\\', '/');
            if (map.Files.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                present.Add(normalized);
            }
            else
            {
                missing.Add(normalized);
            }
        }

        if (missing.Count == 0)
        {
            return ProjectValidationResult.Ok(
                root,
                map.Stack.Kind,
                present,
                template.VerificationCommands);
        }

        return ProjectValidationResult.Invalid(
            root,
            map.Stack.Kind,
            present,
            missing,
            template.VerificationCommands);
    }
}
