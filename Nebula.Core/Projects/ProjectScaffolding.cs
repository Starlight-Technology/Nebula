using Nebula.Core.Agent;

namespace Nebula.Core.Projects;

public sealed record ProjectScaffoldRequest(
    string TemplateId,
    string TargetDirectory,
    string? ProjectName = null);

public sealed record ProjectScaffoldResult(
    bool Success,
    string? Error,
    string TemplateId,
    IReadOnlyList<string> CreatedFiles,
    IReadOnlyList<string> SetupCommands,
    IReadOnlyList<string> VerificationCommands)
{
    public static ProjectScaffoldResult Failed(string templateId, string error) =>
        new(false, error, templateId, [], [], []);
}

public interface IProjectScaffolder
{
    Task<ProjectScaffoldResult> ScaffoldAsync(
        ProjectScaffoldRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectValidationResult(
    bool Success,
    string Root,
    WorkspaceStackKind Stack,
    IReadOnlyList<string> PresentEssentialFiles,
    IReadOnlyList<string> MissingEssentialFiles,
    IReadOnlyList<string> SuggestedCommands)
{
    public static ProjectValidationResult Ok(
        string root,
        WorkspaceStackKind stack,
        IReadOnlyList<string> present,
        IReadOnlyList<string> suggested) =>
        new(true, root, stack, present, [], suggested);

    public static ProjectValidationResult Invalid(
        string root,
        WorkspaceStackKind stack,
        IReadOnlyList<string> present,
        IReadOnlyList<string> missing,
        IReadOnlyList<string> suggested) =>
        new(false, root, stack, present, missing, suggested);
}

public interface IProjectStackValidator
{
    Task<ProjectValidationResult> ValidateAsync(
        string root,
        string templateId,
        CancellationToken cancellationToken = default);
}
