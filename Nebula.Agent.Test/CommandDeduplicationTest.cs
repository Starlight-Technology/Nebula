using Nebula.Agent.Domain;

namespace Nebula.Agent.Test;

public sealed class CommandDeduplicationTest
{
    [Fact]
    public void evaluate_must_allow_first_command_without_history()
    {
        using var workspace = new TempTestWorkspace();
        var history = new ExecutionHistory();

        var result = new CommandDeduplication().Evaluate(
            "Get-ChildItem",
            workspace.Path,
            ExecutionEnvironmentSnapshot.Capture(workspace.Path),
            retryJustification: null,
            history);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void evaluate_must_block_repeated_successful_command_without_workspace_change()
    {
        using var workspace = new TempTestWorkspace();
        var originalSnapshot = ExecutionEnvironmentSnapshot.Capture(workspace.Path);
        var history = new ExecutionHistory();
        history.Add(new ExecutionHistoryEntry
        {
            Command = "Get-ChildItem",
            WorkingDirectory = workspace.Path,
            ExitCode = 0,
            Success = true,
            FileFingerprint = originalSnapshot.FileFingerprint,
            EnvironmentFingerprint = originalSnapshot.EnvironmentFingerprint
        });

        var result = new CommandDeduplication().Evaluate(
            "Get-ChildItem",
            workspace.Path,
            originalSnapshot,
            retryJustification: null,
            history);

        Assert.False(result.Allowed);
        Assert.Contains("same command", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void evaluate_must_allow_repeated_command_when_workspace_changed()
    {
        using var workspace = new TempTestWorkspace();
        var originalSnapshot = ExecutionEnvironmentSnapshot.Capture(workspace.Path);
        var history = new ExecutionHistory();
        history.Add(new ExecutionHistoryEntry
        {
            Command = "Get-ChildItem",
            WorkingDirectory = workspace.Path,
            ExitCode = 0,
            Success = true,
            FileFingerprint = originalSnapshot.FileFingerprint,
            EnvironmentFingerprint = originalSnapshot.EnvironmentFingerprint
        });

        File.WriteAllText(Path.Combine(workspace.Path, "marker.txt"), "x");
        var changedSnapshot = ExecutionEnvironmentSnapshot.Capture(workspace.Path);

        var result = new CommandDeduplication().Evaluate(
            "Get-ChildItem",
            workspace.Path,
            changedSnapshot,
            retryJustification: null,
            history);

        Assert.True(result.Allowed);
        Assert.Contains("changed", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void evaluate_must_allow_repeated_command_with_explicit_retry_justification()
    {
        using var workspace = new TempTestWorkspace();
        var originalSnapshot = ExecutionEnvironmentSnapshot.Capture(workspace.Path);
        var history = new ExecutionHistory();
        history.Add(new ExecutionHistoryEntry
        {
            Command = "Get-ChildItem",
            WorkingDirectory = workspace.Path,
            ExitCode = 0,
            Success = true,
            FileFingerprint = originalSnapshot.FileFingerprint,
            EnvironmentFingerprint = originalSnapshot.EnvironmentFingerprint
        });

        var result = new CommandDeduplication().Evaluate(
            "Get-ChildItem",
            workspace.Path,
            originalSnapshot,
            retryJustification: "Checking the marker file is present now.",
            history);

        Assert.True(result.Allowed);
        Assert.Contains("retry justification", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}