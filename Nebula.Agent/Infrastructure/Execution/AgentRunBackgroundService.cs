using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nebula.Agent.Application;
using Nebula.Agent.Application.Events;
using Nebula.Agent.Data;
using Nebula.Core.Agent;

namespace Nebula.Agent.Infrastructure.Execution;

public sealed class AgentRunBackgroundService : BackgroundService, IAgentRunQueue
{
    private readonly Channel<AgentRunWorkItem> _queue = Channel.CreateUnbounded<AgentRunWorkItem>();
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentRunEventPublisher _eventPublisher;
    private readonly ILogger<AgentRunBackgroundService> _logger;

    public AgentRunBackgroundService(
        IServiceProvider serviceProvider,
        IAgentRunEventPublisher eventPublisher,
        ILogger<AgentRunBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(AgentRunWorkItem workItem, CancellationToken cancellationToken = default)
        => _queue.Writer.WriteAsync(workItem, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent run background service started");

        await foreach (var workItem in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessWorkItemAsync(workItem, stoppingToken);
        }

        _logger.LogInformation("Agent run background service stopped");
    }

    private async Task ProcessWorkItemAsync(AgentRunWorkItem workItem, CancellationToken stoppingToken)
    {
        var runId = workItem.RunId;
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(workItem.CancellationToken, stoppingToken);

        _logger.LogInformation("Processing agent run {RunId}", runId);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IAgentActionRunner>();
            var runStore = scope.ServiceProvider.GetService<IAgentRunStore>();

            var progress = new AgentRunTypedProgressReporter(_eventPublisher, runId);

            var turn = await runner.RunAsync(workItem.Request, progress, linkedCts.Token);

            _eventPublisher.Publish(new AgentRunCompletedEvent(runId, DateTimeOffset.UtcNow, turn));

            if (runStore != null)
            {
                var existingRun = await runStore.GetRunAsync(runId, CancellationToken.None);
                if (existingRun != null)
                {
                    var updatedRun = existingRun with
                    {
                        Status = turn.ActionStatus?.ToString() ?? "Completed",
                        FinishedAt = DateTimeOffset.UtcNow,
                        CurrentPlan = turn.CurrentPlan
                    };
                    await runStore.SaveRunAsync(updatedRun, CancellationToken.None);
                }
            }

            _logger.LogInformation("Agent run {RunId} completed with status {Status}", runId, turn.ActionStatus);
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            _eventPublisher.Publish(new AgentRunCancelledEvent(runId, DateTimeOffset.UtcNow));
            _logger.LogInformation("Agent run {RunId} cancelled", runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run {RunId} failed", runId);
            _eventPublisher.Publish(new AgentRunFailedEvent(runId, DateTimeOffset.UtcNow, ex.Message, null));
        }
        finally
        {
            linkedCts.Dispose();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.Complete();
        await base.StopAsync(cancellationToken);
    }
}