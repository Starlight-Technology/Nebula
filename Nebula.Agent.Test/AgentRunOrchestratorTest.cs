using Moq;

using Nebula.Agent.Application;
using Nebula.Agent.Application.Events;
using Nebula.Core.Agent;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Projects;
using Nebula.Core.Safety;

namespace Nebula.Agent.Test;

public sealed class AgentRunOrchestratorTest
{
    [Fact]
    public async Task run_async_must_enqueue_and_create_running_status()
    {
        var queueMock = new Mock<IAgentRunQueue>();
        var publisherMock = new Mock<IAgentRunEventPublisher>();
        var storeMock = new Mock<IAgentRunStore>();
        AgentRun? savedRun = null;
        storeMock.Setup(s => s.SaveRunAsync(It.IsAny<AgentRun>(), It.IsAny<CancellationToken>()))
            .Callback<AgentRun, CancellationToken>((run, _) => savedRun = run)
            .Returns(Task.CompletedTask);

        var orchestrator = new AgentRunOrchestrator(queueMock.Object, publisherMock.Object, storeMock.Object);

        var request = new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "create file",
            Mode = InteractionMode.Agent,
            ModelName = "test-model",
            MaxSteps = 5,
            WorkspaceRoot = "/tmp/ws"
        };

        var runId = await orchestrator.RunAsync(request);

        Assert.NotEqual(Guid.Empty, runId);
        queueMock.Verify(q => q.EnqueueAsync(It.Is<AgentRunWorkItem>(w => w.RunId == runId && w.Request == request), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(savedRun);
        Assert.Equal(runId, savedRun!.Id);
        Assert.Equal("Running", savedRun.Status);
        Assert.Equal(request.Prompt, savedRun.Prompt);
        Assert.Equal(request.WorkspaceRoot, savedRun.WorkspaceRoot);

        var status = await orchestrator.GetStatusAsync(runId);
        Assert.NotNull(status);
        Assert.Equal("Running", status!.Status);
        Assert.Equal(runId, status.RunId);
    }

    [Fact]
    public async Task run_async_without_store_must_still_enqueue()
    {
        var queueMock = new Mock<IAgentRunQueue>();
        var publisherMock = new Mock<IAgentRunEventPublisher>();
        var orchestrator = new AgentRunOrchestrator(queueMock.Object, publisherMock.Object, null);

        var request = new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "prompt",
            Mode = InteractionMode.Agent
        };

        var runId = await orchestrator.RunAsync(request);

        queueMock.Verify(q => q.EnqueueAsync(It.IsAny<AgentRunWorkItem>(), It.IsAny<CancellationToken>()), Times.Once);
        var status = await orchestrator.GetStatusAsync(runId);
        Assert.NotNull(status);
    }

    [Fact]
    public async Task run_dry_run_must_enqueue_with_dryrun_true()
    {
        var queueMock = new Mock<IAgentRunQueue>();
        AgentRunWorkItem? captured = null;
        queueMock.Setup(q => q.EnqueueAsync(It.IsAny<AgentRunWorkItem>(), It.IsAny<CancellationToken>()))
            .Callback<AgentRunWorkItem, CancellationToken>((w, _) => captured = w)
            .Returns(ValueTask.CompletedTask);

        var orchestrator = new AgentRunOrchestrator(queueMock.Object, Mock.Of<IAgentRunEventPublisher>());

        var request = new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "dry",
            Mode = InteractionMode.Agent
        };

        var runId = await orchestrator.RunDryRunAsync(request);

        Assert.NotNull(captured);
        Assert.True(captured!.Request.DryRun);
        Assert.Equal(runId, captured.RunId);
    }

    [Fact]
    public async Task resume_async_must_build_resume_context_and_enqueue()
    {
        var queueMock = new Mock<IAgentRunQueue>();
        AgentRunWorkItem? captured = null;
        queueMock.Setup(q => q.EnqueueAsync(It.IsAny<AgentRunWorkItem>(), It.IsAny<CancellationToken>()))
            .Callback<AgentRunWorkItem, CancellationToken>((w, _) => captured = w)
            .Returns(ValueTask.CompletedTask);

        var orchestrator = new AgentRunOrchestrator(queueMock.Object, Mock.Of<IAgentRunEventPublisher>());

        var run = new AgentRun(
            Id: Guid.NewGuid(),
            ConversationId: Guid.NewGuid(),
            RequestId: Guid.NewGuid(),
            Prompt: "original prompt",
            ModelName: "m",
            Status: "Failed",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishedAt: DateTimeOffset.UtcNow,
            Response: null,
            IsCancelled: false,
            Steps: new List<AgentStepRecord>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), 1, 1, OperationKind.TerminalCommand, "obj", "cmd", "/tmp", null, 0, true, DateTimeOffset.UtcNow, "out", null, "bash", CommandSafetyDecisionType.Allow, false, false)
            },
            CurrentPlan: "#1 [completed] do thing",
            Artifacts: [],
            Approvals: [],
            WorkspaceRoot: "/tmp/ws"
        );

        var runId = await orchestrator.ResumeAsync(run);

        Assert.NotNull(captured);
        Assert.Contains("[resumed_task]", captured!.Request.ChatHistoryContext);
        Assert.Contains("original prompt", captured.Request.Prompt);
        Assert.Equal(InteractionMode.Agent, captured.Request.Mode);
        Assert.Equal("/tmp/ws", captured.Request.WorkspaceRoot);
    }

    [Fact]
    public async Task run_approved_command_async_must_create_approved_action()
    {
        var queueMock = new Mock<IAgentRunQueue>();
        AgentRunWorkItem? captured = null;
        queueMock.Setup(q => q.EnqueueAsync(It.IsAny<AgentRunWorkItem>(), It.IsAny<CancellationToken>()))
            .Callback<AgentRunWorkItem, CancellationToken>((w, _) => captured = w)
            .Returns(ValueTask.CompletedTask);

        var orchestrator = new AgentRunOrchestrator(queueMock.Object, Mock.Of<IAgentRunEventPublisher>());

        var command = new CommandExecution
        {
            Objective = "list files",
            Run = "ls -la",
            OperationKind = OperationKind.TerminalCommand,
            TargetPath = "/tmp/file.txt",
            Content = "content",
            Language = "bash",
            TemplateId = "tpl",
            WorkingDirectory = "/tmp",
            PlannedFiles = new List<PlannedPatchFile> { new("a.txt", "hello") }
        };

        var runId = await orchestrator.RunApprovedCommandAsync(command, ApprovalScope.Workspace);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Request.ApprovedAction);
        Assert.Equal("list files", captured.Request.ApprovedAction!.Objective);
        Assert.Equal("ls -la", captured.Request.ApprovedAction.Command);
        Assert.Equal(OperationKind.TerminalCommand, captured.Request.ApprovedAction.OperationKind);
        Assert.Equal("/tmp/file.txt", captured.Request.ApprovedAction.TargetPath);
        Assert.Equal(1, captured.Request.ApprovedAction.PlannedFiles.Count);
        Assert.Equal(ApprovalScope.Workspace, captured.Request.ApprovedAction.Scope);
        Assert.Equal(InteractionMode.Agent, captured.Request.Mode);
        Assert.Contains("Executar comando aprovado", captured.Request.Prompt);
    }

    [Fact]
    public async Task get_status_async_must_return_null_for_unknown_run()
    {
        var orchestrator = new AgentRunOrchestrator(Mock.Of<IAgentRunQueue>(), Mock.Of<IAgentRunEventPublisher>());

        var status = await orchestrator.GetStatusAsync(Guid.NewGuid());

        Assert.Null(status);
    }

    [Fact]
    public async Task cancel_async_must_update_status_to_cancelled()
    {
        var queueMock = new Mock<IAgentRunQueue>();
        var publisherMock = new Mock<IAgentRunEventPublisher>();

        var orchestrator = new AgentRunOrchestrator(queueMock.Object, publisherMock.Object);

        var request = new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "p",
            Mode = InteractionMode.Agent
        };

        var runId = await orchestrator.RunAsync(request);
        await orchestrator.CancelAsync(runId);

        var status = await orchestrator.GetStatusAsync(runId);
        Assert.NotNull(status);
        Assert.Equal("Cancelled", status!.Status);
    }

    [Fact]
    public async Task subscribe_async_must_delegate_to_publisher()
    {
        var publisherMock = new Mock<IAgentRunEventPublisher>();
        var expected = new List<AgentRunEvent>();
        publisherMock.Setup(p => p.Subscribe(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(expected.ToAsyncEnumerable());

        var orchestrator = new AgentRunOrchestrator(Mock.Of<IAgentRunQueue>(), publisherMock.Object);

        var runId = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        var count = 0;
        await foreach (var _ in orchestrator.SubscribeAsync(runId, cts.Token))
        {
            count++;
        }

        Assert.Equal(0, count);
        publisherMock.Verify(p => p.Subscribe(runId, It.IsAny<CancellationToken>()), Times.Once);
    }
}

internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
