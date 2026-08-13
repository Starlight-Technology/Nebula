namespace Nebula.Agent.Application;

/// <summary>
/// Public snapshot of a completed agent run used for post-task learning.
/// Kept as plain data so the learning service does not depend on internal types.
/// </summary>
public sealed record PostTaskRunSnapshot(
    string Objective,
    IReadOnlyList<string> SuccessfulCommands,
    IReadOnlyList<string> ArtifactNames);

public interface IPostTaskLearningService
{
    /// <summary>
    /// Learns a natural-language summary of what the agent actually ran,
    /// persisted as a reusable knowledge item. Non-fatal.
    /// </summary>
    Task<bool> TryLearnFromRunAsync(
        PostTaskRunSnapshot snapshot,
        CancellationToken cancellationToken = default);
}