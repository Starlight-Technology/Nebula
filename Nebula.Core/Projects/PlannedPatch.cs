using System.Text.Json.Serialization;

namespace Nebula.Core.Projects;

public sealed record PlannedPatchFile(
    [property: JsonPropertyName("path")] string RelativePath,
    [property: JsonPropertyName("content")] string Content);

public sealed record PlannedPatch(
    string Objective,
    IReadOnlyList<PlannedPatchFile> Files,
    string? TargetDirectory = null);

public sealed record PlannedPatchRequest(
    string Objective,
    IReadOnlyList<PlannedPatchFile> Files,
    string TargetDirectory);

public sealed record PlannedPatchResult(
    bool Success,
    string? Error,
    IReadOnlyList<string> AppliedFiles)
{
    public static PlannedPatchResult Failed(string error) =>
        new(false, error, []);
}

public interface IPlannedPatchApplier
{
    Task<PlannedPatchResult> ApplyAsync(
        PlannedPatchRequest request,
        CancellationToken cancellationToken = default);
}
