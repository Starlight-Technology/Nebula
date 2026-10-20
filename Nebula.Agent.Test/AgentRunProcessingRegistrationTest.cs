using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

using Nebula.Agent;
using Nebula.Agent.Application;
using Nebula.Agent.Application.Events;
using Nebula.Agent.Infrastructure.Events;
using Nebula.Agent.Infrastructure.Execution;
using Nebula.Core.Agent;
using Nebula.Core.Interactions;

namespace Nebula.Agent.Test;

public sealed class AgentRunProcessingRegistrationTest
{
    [Fact]
    public void add_agent_run_processing_must_share_the_queue_and_hosted_worker_instance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentRunProcessing();

        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IAgentRunQueue>();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        Assert.Same(queue, hostedService);
        Assert.IsType<AgentRunBackgroundService>(queue);
    }

    [Fact]
    public async Task event_publisher_must_replay_plan_and_completion_published_before_subscription()
    {
        using var publisher = new ChannelAgentRunEventPublisher();
        var runId = Guid.NewGuid();
        var planEvent = new AgentRunPlanUpdatedEvent(
            runId,
            DateTimeOffset.UtcNow,
            [new AgentPlanStep { Id = 1, Description = "Read status" }]);
        var completedTurn = new ConversationTurn
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "Read the project status.",
            Mode = Nebula.Core.Interactions.InteractionMode.Agent,
            Response = "Project status read.",
            ActionStatus = ActionExecutionStatus.Completed
        };
        var completedEvent = new AgentRunCompletedEvent(
            runId,
            DateTimeOffset.UtcNow,
            completedTurn);
        publisher.Publish(planEvent);
        publisher.Publish(completedEvent);

        var events = new List<AgentRunEvent>();
        await foreach (var evt in publisher.Subscribe(runId))
        {
            events.Add(evt);
            if (evt is AgentRunCompletedEvent)
            {
                break;
            }
        }

        Assert.Equal([planEvent, completedEvent], events);
    }

    [Fact]
    public async Task intermediate_blocked_progress_must_not_publish_terminal_events()
    {
        var finalTurn = new ConversationTurn
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "List the files in the current directory.",
            Mode = InteractionMode.Agent,
            Response = "The files were listed.",
            ActionStatus = ActionExecutionStatus.Completed
        };
        var runnerMock = new Mock<IAgentActionRunner>();
        runnerMock
            .Setup(runner => runner.RunAsync(
                It.IsAny<AgentActionRunRequest>(),
                It.IsAny<IProgress<ConversationTurn>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<AgentActionRunRequest, IProgress<ConversationTurn>?, CancellationToken>(
                (request, progress, _) =>
                {
                    progress?.Report(new ConversationTurn
                    {
                        ConversationId = request.ConversationId,
                        RequestId = request.RequestId,
                        Prompt = request.Prompt,
                        Mode = InteractionMode.Agent,
                        Response = "The same command was already executed successfully.",
                        ActionStatus = ActionExecutionStatus.Blocked,
                        ActionEvents =
                        [
                            new ActionExecutionEvent
                            {
                                Kind = ActionExecutionEventKind.DeduplicationBlocked,
                                Status = ActionExecutionStatus.Blocked,
                                Title = "Repeated command blocked",
                                Message = "The same command was already executed successfully.",
                                IsError = true
                            }
                        ]
                    });
                    return Task.FromResult(finalTurn);
                });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentRunProcessing();
        services.AddScoped<IAgentActionRunner>(_ => runnerMock.Object);

        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        await worker.StartAsync(CancellationToken.None);

        using (var scope = provider.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentRunOrchestrator>();
            var runId = await orchestrator.RunAsync(new AgentActionRunRequest
            {
                ConversationId = finalTurn.ConversationId,
                RequestId = finalTurn.RequestId,
                Prompt = finalTurn.Prompt,
                Mode = InteractionMode.Agent,
                ModelName = "test-model"
            });

            var events = new List<AgentRunEvent>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var evt in orchestrator.SubscribeAsync(runId, timeout.Token))
            {
                events.Add(evt);
                if (evt is AgentRunCompletedEvent)
                {
                    break;
                }
            }

            Assert.DoesNotContain(events, evt => evt is AgentRunFailedEvent);
            Assert.DoesNotContain(events, evt => evt is AgentRunCancelledEvent);
            var completed = Assert.Single(events.OfType<AgentRunCompletedEvent>());
            Assert.Same(finalTurn, completed.FinalTurn);
        }

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task shared_run_services_must_execute_enqueued_work_and_return_plan_events()
    {
        var finalTurn = new ConversationTurn
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "Read the project status.",
            Mode = InteractionMode.Agent,
            Response = "Project status read.",
            ActionStatus = ActionExecutionStatus.Completed,
            CurrentPlan = "#1 [completed] [risk=low] Read status"
        };
        var runnerMock = new Mock<IAgentActionRunner>();
        runnerMock
            .Setup(runner => runner.RunAsync(
                It.IsAny<AgentActionRunRequest>(),
                It.IsAny<IProgress<ConversationTurn>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<AgentActionRunRequest, IProgress<ConversationTurn>?, CancellationToken>(
                (request, progress, _) =>
                {
                    progress?.Report(new ConversationTurn
                    {
                        ConversationId = request.ConversationId,
                        RequestId = request.RequestId,
                        Prompt = request.Prompt,
                        Mode = InteractionMode.Agent,
                        Reasoning = "Reading status.",
                        ActionStatus = ActionExecutionStatus.Planning,
                        CurrentPlan = "#1 [pending] [risk=low] Read status"
                    });
                    return Task.FromResult(finalTurn);
                });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentRunProcessing();
        services.AddScoped<IAgentActionRunner>(_ => runnerMock.Object);

        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<IHostedService>();
        await worker.StartAsync(CancellationToken.None);

        Guid runId;
        using (var scope = provider.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentRunOrchestrator>();
            runId = await orchestrator.RunAsync(new AgentActionRunRequest
            {
                ConversationId = finalTurn.ConversationId,
                RequestId = finalTurn.RequestId,
                Prompt = finalTurn.Prompt,
                Mode = InteractionMode.Agent,
                ModelName = "test-model"
            });

            var events = new List<AgentRunEvent>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var evt in orchestrator.SubscribeAsync(runId, timeout.Token))
            {
                events.Add(evt);
                if (evt is AgentRunCompletedEvent)
                {
                    break;
                }
            }

            Assert.Contains(events, evt => evt is AgentRunPlanUpdatedEvent);
            Assert.Contains(events, evt => evt is AgentRunCompletedEvent completed &&
                ReferenceEquals(completed.FinalTurn, finalTurn));
        }

        await worker.StopAsync(CancellationToken.None);
        runnerMock.Verify(runner => runner.RunAsync(
            It.IsAny<AgentActionRunRequest>(),
            It.IsAny<IProgress<ConversationTurn>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}