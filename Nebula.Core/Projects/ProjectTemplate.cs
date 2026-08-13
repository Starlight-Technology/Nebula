using Nebula.Core.Agent;

namespace Nebula.Core.Projects;

public sealed record TemplateFile(
    string RelativePath,
    string Content);

public sealed record ProjectTemplate(
    string Id,
    string Name,
    string Description,
    WorkspaceStackKind Stack,
    IReadOnlyList<TemplateFile> Files,
    IReadOnlyList<string> SetupCommands,
    IReadOnlyList<string> VerificationCommands,
    IReadOnlyList<string> EssentialFiles,
    IReadOnlyList<string> Keywords);

public interface IProjectTemplateCatalog
{
    IReadOnlyList<ProjectTemplate> GetAll();

    ProjectTemplate? FindById(string id);

    ProjectTemplate? Suggest(string objective, WorkspaceStackKind? stack = null);
}
