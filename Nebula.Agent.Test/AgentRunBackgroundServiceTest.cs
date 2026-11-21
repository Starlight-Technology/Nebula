using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

using Nebula.Agent.Application;
using Nebula.Agent.Application.Events;
using Nebula.Core.Agent;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Safety;

namespace Nebula.Agent.Test;

public sealed class AgentRunBackgroundServiceTest
{
    [Fact]
    public async Task failed_run_must_be_persisted_as_failed()
    {
        var runStore = new RecordingRunStore();
        var runner = new Mock<IAgentActionRunner>();
        runner
            .Setup(value => value.RunAsync(
                It.IsAny<AgentActionRunRequest>(),
                It.IsAny<IProgress<ConversationTurn>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing."));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentRunProcessing();
        services.AddSingleton<IAgentRunStore>(runStore);
        services.AddScoped(_ => runner.Object);

        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            using var scope = provider.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentRunOrchestrator>();
            var runId = await orchestrator.RunAsync(CreateRequest());

            var events = await CollectUntilTerminalAsync(orchestrator, runId);

            Assert.Contains(events, evt => evt is AgentRunFailedEvent);
            var saved = await WaitForStatusAsync(runStore, runId, "Failed");
            Assert.NotNull(saved.FinishedAt);
            Assert.False(saved.IsCancelled);
            Assert.Contains("HttpClient.Timeout", saved.Response);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task cancelled_run_must_be_persisted_as_cancelled()
    {
        var runStore = new RecordingRunStore();
        var runner = new Mock<IAgentActionRunner>();
        runner
            .Setup(value => value.RunAsync(
                It.IsAny<AgentActionRunRequest>(),
                It.IsAny<IProgress<ConversationTurn>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<AgentActionRunRequest, IProgress<ConversationTurn>?, CancellationToken>(
                async (_, _, cancellationToken) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentRunProcessing();
        services.AddSingleton<IAgentRunStore>(runStore);
        services.AddScoped(_ => runner.Object);

        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            using var scope = provider.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentRunOrchestrator>();
            using var cancellation = new CancellationTokenSource();
            var runId = await orchestrator.RunAsync(
                CreateRequest(),
                cancellation.Token);

            var eventsTask = CollectUntilTerminalAsync(orchestrator, runId);
            await Task.Delay(100);
            cancellation.Cancel();
            var events = await eventsTask;

            Assert.Contains(events, evt => evt is AgentRunCancelledEvent);
            var saved = await WaitForStatusAsync(runStore, runId, "Cancelled");
            Assert.True(saved.IsCancelled);
            Assert.NotNull(saved.FinishedAt);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task returned_failed_turn_must_publish_failed_event()
    {
        var runStore = new RecordingRunStore();
        var runner = new Mock<IAgentActionRunner>();
        runner
            .Setup(value => value.RunAsync(
                It.IsAny<AgentActionRunRequest>(),
                It.IsAny<IProgress<ConversationTurn>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<AgentActionRunRequest, IProgress<ConversationTurn>?, CancellationToken>(
                (request, _, _) => Task.FromResult(new ConversationTurn
                {
                    ConversationId = request.ConversationId,
                    RequestId = request.RequestId,
                    Prompt = request.Prompt,
                    Mode = InteractionMode.Agent,
                    ModelName = "test-model",
                    Response = "The build failed.",
                    ActionStatus = ActionExecutionStatus.Failed,
                    IsCancelled = false
                }));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentRunProcessing();
        services.AddSingleton<IAgentRunStore>(runStore);
        services.AddScoped(_ => runner.Object);

        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            using var scope = provider.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentRunOrchestrator>();
            var runId = await orchestrator.RunAsync(CreateRequest());

            var events = await CollectUntilTerminalAsync(orchestrator, runId);

            Assert.Contains(events, evt => evt is AgentRunFailedEvent);
            Assert.DoesNotContain(events, evt => evt is AgentRunCompletedEvent);
            var saved = await WaitForStatusAsync(runStore, runId, "Failed");
            Assert.False(saved.IsCancelled);
            Assert.Equal("The build failed.", saved.Response);
            Assert.NotNull(saved.FinishedAt);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task returned_cancelled_turn_must_publish_cancelled_event_with_final_turn()
    {
        var runStore = new RecordingRunStore();
        var runner = new Mock<IAgentActionRunner>();
        runner
            .Setup(value => value.RunAsync(
                It.IsAny<AgentActionRunRequest>(),
                It.IsAny<IProgress<ConversationTurn>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<AgentActionRunRequest, IProgress<ConversationTurn>?, CancellationToken>(
                (request, _, _) => Task.FromResult(new ConversationTurn
                {
                    ConversationId = request.ConversationId,
                    RequestId = request.RequestId,
                    Prompt = request.Prompt,
                    Mode = InteractionMode.Agent,
                    ModelName = "test-model",
                    Response = "Cancelled by the user.",
                    ActionStatus = ActionExecutionStatus.Cancelled,
                    IsCancelled = true
                }));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentRunProcessing();
        services.AddSingleton<IAgentRunStore>(runStore);
        services.AddScoped(_ => runner.Object);

        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            using var scope = provider.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentRunOrchestrator>();
            var runId = await orchestrator.RunAsync(CreateRequest());

            var events = await CollectUntilTerminalAsync(orchestrator, runId);

            var cancelled = Assert.Single(events.OfType<AgentRunCancelledEvent>());
            Assert.NotNull(cancelled.FinalTurn);
            Assert.DoesNotContain(events, evt => evt is AgentRunCompletedEvent);
            var saved = await WaitForStatusAsync(runStore, runId, "Cancelled");
            Assert.True(saved.IsCancelled);
            Assert.Equal("Cancelled by the user.", saved.Response);
            Assert.NotNull(saved.FinishedAt);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task startup_must_mark_interrupted_running_runs_as_failed()
    {
        var staleRun = new AgentRun(
            Id: Guid.NewGuid(),
            ConversationId: Guid.NewGuid(),
            RequestId: Guid.NewGuid(),
            Prompt: "Old task",
            ModelName: "test-model",
            Status: "Running",
            StartedAt: DateTimeOffset.UtcNow.AddHours(-2),
            FinishedAt: null,
            Response: null,
            IsCancelled: false,
            Steps: [],
            CurrentPlan: "",
            Artifacts: [],
            Approvals: [],
            WorkspaceRoot: "/tmp/nebula-test");

        var runStore = new RecordingRunStore
        {
            StaleRunningRuns = [staleRun]
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentRunProcessing();
        services.AddSingleton<IAgentRunStore>(runStore);
        services.AddScoped(_ => new Mock<IAgentActionRunner>().Object);

        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        await worker.StartAsync(CancellationToken.None);

        try
        {
            var saved = await WaitForStatusAsync(runStore, staleRun.Id, "Failed");
            Assert.NotNull(saved.FinishedAt);
            Assert.Contains("restarted", saved.Response);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static AgentActionRunRequest CreateRequest()
    {
        return new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "Create the calculator files.",
            Mode = InteractionMode.Agent,
            ModelName = "test-model",
            WorkspaceRoot = Path.Combine(
                Path.GetTempPath(),
                $"nebula-background-{Guid.NewGuid():N}")
        };
    }

    private static async Task<List<AgentRunEvent>> CollectUntilTerminalAsync(
        IAgentRunOrchestrator orchestrator,
        Guid runId)
    {
        var events = new List<AgentRunEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in orchestrator.SubscribeAsync(runId, timeout.Token))
        {
            events.Add(evt);
            if (evt is AgentRunCompletedEvent or AgentRunFailedEvent or AgentRunCancelledEvent)
            {
                break;
            }
        }

        return events;
    }

    private static async Task<AgentRun> WaitForStatusAsync(
        RecordingRunStore store,
        Guid runId,
        string status)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = await store.GetRunAsync(runId);
            if (run?.Status == status)
            {
                return run;
            }

            await Task.Delay(25);
        }

        var last = await store.GetRunAsync(runId);
        Assert.Fail(
            $"Run {runId} did not reach status {status}. Last status: {last?.Status ?? "missing"}.");
        return last!;
    }

    private sealed class RecordingRunStore : IAgentRunStore
    {
        private readonly Dictionary<Guid, AgentRun> _runs = [];

        public IReadOnlyList<AgentRun> StaleRunningRuns { get; set; } = [];

        public Task SaveRunAsync(
            AgentRun run,
            CancellationToken cancellationToken = default)
        {
            _runs[run.Id] = run;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentRun>> GetRunsAsync(
            int limit = 20,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentRun>>(
                _runs.Values.OrderByDescending(run => run.StartedAt).Take(limit).ToList());
        }

        public Task<AgentRun?> GetRunAsync(
            Guid runId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_runs.GetValueOrDefault(runId));
        }

        public Task<IReadOnlyList<AgentRun>> GetUnfinishedRunsAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentRun>>(
                _runs.Values
                    .Where(run => run.ConversationId == conversationId &&
                                  run.FinishedAt is null)
                    .ToList());
        }

        public Task<IReadOnlyList<AgentRun>> GetInterruptedRunsAsync(
            CancellationToken cancellationToken = default)
        {
            var running = StaleRunningRuns
                .Concat(_runs.Values.Where(run =>
                    AgentRunStates.IsInterruptedCandidate(run.Status) &&
                    run.FinishedAt is null))
                .GroupBy(run => run.Id)
                .Select(group => group.First())
                .ToList();
            return Task.FromResult<IReadOnlyList<AgentRun>>(running);
        }

        public Task<Guid?> GetRunIdByStepIdAsync(
            Guid stepId,
            CancellationToken cancellationToken = default)
        {
            var run = _runs.Values.FirstOrDefault(
                value => value.Steps.Any(step => step.Id == stepId));
            return Task.FromResult(run?.Id);
        }

        public Task<IReadOnlyList<AgentRun>> GetRunsByConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentRun>>(
                _runs.Values
                    .Where(run => run.ConversationId == conversationId)
                    .OrderByDescending(run => run.StartedAt)
                    .Take(10)
                    .ToList());
        }

        public Task<AgentArtifactRecord?> GetArtifactAsync(
            Guid artifactId,
            CancellationToken cancellationToken = default)
        {
            var artifact = _runs.Values
                .SelectMany(run => run.Artifacts ?? [])
                .FirstOrDefault(value => value.Id == artifactId);
            return Task.FromResult(artifact);
        }
    }
}
