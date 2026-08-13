using Microsoft.EntityFrameworkCore;

using Nebula.Core.Agent;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Postgres.Context;

namespace Nebula.Agent.Test.Safety;

public sealed class PostgresAgentRunStoreTest
{
    [Fact]
    public async Task save_and_get_run_must_round_trip_steps()
    {
        var context = CreateContext();
        var store = new PostgresAgentRunStore(context);
        var run = CreateRun();

        await store.SaveRunAsync(run);
        var loaded = await store.GetRunAsync(run.Id);

        Assert.NotNull(loaded);
        Assert.Equal(run.Prompt, loaded.Prompt);
        Assert.Equal(run.Status, loaded.Status);
        Assert.Equal(run.ModelName, loaded.ModelName);
        Assert.Equal(2, loaded.Steps.Count);
        Assert.Equal(OperationKind.ScriptContent, loaded.Steps[0].OperationKind);
        Assert.Equal("write-file \"hello.py\"", loaded.Steps[0].Command);
        Assert.True(loaded.Steps[0].Success);
        Assert.Equal("TerminalCommand", loaded.Steps[1].OperationKind.ToString());
        Assert.Equal(2, loaded.Steps[1].Step);
        Assert.Equal("dotnet test", loaded.Steps[1].Command);
    }

    [Fact]
    public async Task get_runs_must_return_most_recent_first()
    {
        var context = CreateContext();
        var store = new PostgresAgentRunStore(context);
        var older = CreateRun(status: "Completed", minutesAgo: 20);
        var newer = CreateRun(status: "Failed", minutesAgo: 5);

        await store.SaveRunAsync(older);
        await store.SaveRunAsync(newer);

        var runs = await store.GetRunsAsync(limit: 10);

        Assert.Equal(2, runs.Count);
        Assert.Equal(newer.Id, runs[0].Id);
        Assert.Equal(older.Id, runs[1].Id);
    }

    [Fact]
    public async Task update_run_must_replace_steps_and_status()
    {
        var context = CreateContext();
        var store = new PostgresAgentRunStore(context);
        var run = CreateRun(status: "Started");

        await store.SaveRunAsync(run);

        var updated = run with
        {
            Status = "Completed",
            FinishedAt = DateTimeOffset.UtcNow,
            Response = "done",
            Steps =
            [
                run.Steps[0] with
                {
                    Id = Guid.NewGuid(),
                    Success = true,
                    ExitCode = 0
                }
            ]
        };
        await store.SaveRunAsync(updated);

        var loaded = await store.GetRunAsync(run.Id);

        Assert.Equal("Completed", loaded!.Status);
        Assert.Equal("done", loaded.Response);
        Assert.Single(loaded.Steps);
    }

    [Fact]
    public async Task save_and_get_run_must_round_trip_plan_artifacts_and_approvals()
    {
        var context = CreateContext();
        var store = new PostgresAgentRunStore(context);
        var runId = Guid.NewGuid();
        var run = CreateRun(status: "AwaitingApproval", minutesAgo: 2) with
        {
            Id = runId,
            Status = "AwaitingApproval",
            FinishedAt = null,
            CurrentPlan = "1. Create hello.py - completed.",
            Artifacts =
            [
                new AgentArtifactRecord(
                    Guid.NewGuid(),
                    runId,
                    "hello.py",
                    "C:\\work\\hello.py",
                    "abc123",
                    DateTimeOffset.UtcNow)
            ],
            Approvals =
            [
                new AgentApprovalRecord(
                    Guid.NewGuid(),
                    runId,
                    Guid.NewGuid(),
                    "Run tests",
                    "dotnet test",
                    CommandSafetyDecisionType.AskApproval,
                    ApprovedByUser: true,
                    AutoApproved: false,
                    DateTimeOffset.UtcNow)
            ],
            WorkspaceRoot = @"C:\work"
        };

        await store.SaveRunAsync(run);
        var loaded = await store.GetRunAsync(runId);

        Assert.NotNull(loaded);
        Assert.Equal(run.CurrentPlan, loaded.CurrentPlan);
        Assert.Equal(@"C:\work", loaded.WorkspaceRoot);
        var artifact = Assert.Single(loaded.Artifacts);
        Assert.Equal("hello.py", artifact.Name);
        Assert.Equal("abc123", artifact.ContentHash);
        var approval = Assert.Single(loaded.Approvals);
        Assert.True(approval.ApprovedByUser);
        Assert.False(approval.AutoApproved);
        Assert.Equal("dotnet test", approval.Command);
        Assert.Equal(CommandSafetyDecisionType.AskApproval, approval.Decision);
    }

    [Fact]
    public async Task get_unfinished_runs_must_return_only_incomplete_runs_of_conversation()
    {
        var context = CreateContext();
        var store = new PostgresAgentRunStore(context);
        var conversationId = Guid.NewGuid();
        var inProgress = CreateRun(status: "Executing", minutesAgo: 1) with
        {
            ConversationId = conversationId,
            FinishedAt = null
        };
        var finished = CreateRun(status: "Completed", minutesAgo: 5) with
        {
            ConversationId = conversationId,
            FinishedAt = DateTimeOffset.UtcNow
        };
        var otherConversation = CreateRun(status: "Executing", minutesAgo: 3) with
        {
            ConversationId = Guid.NewGuid(),
            FinishedAt = null
        };

        await store.SaveRunAsync(finished);
        await store.SaveRunAsync(otherConversation);
        await store.SaveRunAsync(inProgress);

        var unfinished = await store.GetUnfinishedRunsAsync(conversationId);

        var run = Assert.Single(unfinished);
        Assert.Equal(inProgress.Id, run.Id);
        Assert.Null(run.FinishedAt);
    }

    [Fact]
    public async Task save_and_get_run_must_round_trip_step_safety_fields()
    {
        var context = CreateContext();
        var store = new PostgresAgentRunStore(context);
        var runId = Guid.NewGuid();
        var run = CreateRun() with
        {
            Id = runId,
            Steps =
            [
                new AgentStepRecord(
                    Guid.NewGuid(),
                    runId,
                    1,
                    1,
                    OperationKind.TerminalCommand,
                    "Run a safe command",
                    "Get-ChildItem",
                    null,
                    null,
                    0,
                    true,
                    DateTimeOffset.UtcNow,
                    "files",
                    null,
                    Shell: "powershell",
                    SafetyDecision: CommandSafetyDecisionType.Allow,
                    ApprovedByUser: true,
                    AutoApproved: false)
            ]
        };

        await store.SaveRunAsync(run);
        var loaded = await store.GetRunAsync(runId);

        var step = Assert.Single(loaded!.Steps);
        Assert.Equal("powershell", step.Shell);
        Assert.Equal(CommandSafetyDecisionType.Allow, step.SafetyDecision);
        Assert.True(step.ApprovedByUser);
        Assert.False(step.AutoApproved);
    }

    private static AgentRun CreateRun(
        string status = "Completed",
        int minutesAgo = 10)
    {
        var runId = Guid.NewGuid();
        return new AgentRun(
            runId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Create a script and run the tests.",
            "deepseek-r1:7b",
            status,
            DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            DateTimeOffset.UtcNow.AddMinutes(-minutesAgo + 1),
            "Tarefa concluida.",
            false,
            [
                new AgentStepRecord(
                    Guid.NewGuid(),
                    runId,
                    1,
                    1,
                    OperationKind.ScriptContent,
                    "Create hello.py",
                    "write-file \"hello.py\"",
                    null,
                    "C:\\work\\hello.py",
                    0,
                    true,
                    DateTimeOffset.UtcNow.AddMinutes(-minutesAgo + 0.2),
                    "File written: C:\\work\\hello.py",
                    null),
                new AgentStepRecord(
                    Guid.NewGuid(),
                    runId,
                    2,
                    1,
                    OperationKind.TerminalCommand,
                    "Run the tests",
                    "dotnet test",
                    null,
                    null,
                    0,
                    true,
                    DateTimeOffset.UtcNow.AddMinutes(-minutesAgo + 0.5),
                    "Passed! 200 passed.",
                    null)
            ]);
    }

    private static PostgresContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PostgresContext>()
            .UseInMemoryDatabase($"nebula-runs-{Guid.NewGuid():N}")
            .Options;
        return new PostgresContext(options);
    }
}
