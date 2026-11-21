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

        await ReconcileInterruptedRunsAsync(DateTimeOffset.UtcNow, stoppingToken);

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

        using var scope = _serviceProvider.CreateScope();
        var runStore = scope.ServiceProvider.GetService<IAgentRunStore>();

        try
        {
            var runner = scope.ServiceProvider.GetRequiredService<IAgentActionRunner>();

            var progress = new AgentRunTypedProgressReporter(_eventPublisher, runId);

            var turn = await runner.RunAsync(workItem.Request, progress, linkedCts.Token);

            if (turn.IsCancelled || turn.ActionStatus == ActionExecutionStatus.Cancelled)
            {
                const string cancelledResponse = "Run cancelado antes da conclusão.";
                await PersistTerminalStatusAsync(
                    runStore,
                    runId,
                    status: "Cancelled",
                    response: turn.Response ?? cancelledResponse,
                    isCancelled: true,
                    overwriteFinished: true);
                _eventPublisher.Publish(
                    new AgentRunCancelledEvent(runId, DateTimeOffset.UtcNow, turn));
                _logger.LogInformation("Agent run {RunId} cancelled", runId);
                return;
            }

            if (turn.ActionStatus is
                ActionExecutionStatus.Failed or
                ActionExecutionStatus.Unsafe or
                ActionExecutionStatus.Blocked)
            {
                var status = turn.ActionStatus.Value.ToString();
                var response = turn.Response ?? $"Agent run finished with status {status}.";
                await PersistTerminalStatusAsync(
                    runStore,
                    runId,
                    status,
                    response,
                    isCancelled: false,
                    overwriteFinished: true);
                _eventPublisher.Publish(
                    new AgentRunFailedEvent(
                        runId,
                        DateTimeOffset.UtcNow,
                        response,
                        turn));
                _logger.LogInformation(
                    "Agent run {RunId} finished with failure status {Status}",
                    runId,
                    status);
                return;
            }

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
            await PersistTerminalStatusAsync(
                runStore,
                runId,
                status: "Cancelled",
                response: "Run cancelado antes da conclusão.",
                isCancelled: true);
            _eventPublisher.Publish(new AgentRunCancelledEvent(runId, DateTimeOffset.UtcNow));
            _logger.LogInformation("Agent run {RunId} cancelled", runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run {RunId} failed", runId);
            await PersistTerminalStatusAsync(
                runStore,
                runId,
                status: "Failed",
                response: $"Agent run failed: {ex.Message}",
                isCancelled: false);
            _eventPublisher.Publish(new AgentRunFailedEvent(runId, DateTimeOffset.UtcNow, ex.Message, null));
        }
        finally
        {
            linkedCts.Dispose();
        }
    }

    private async Task ReconcileInterruptedRunsAsync(
        DateTimeOffset startupUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var runStore = scope.ServiceProvider.GetService<IAgentRunStore>();
            if (runStore is null)
            {
                return;
            }

            var runningRuns = await runStore.GetInterruptedRunsAsync(cancellationToken);
            var interrupted = 0;
            var cutoff = startupUtc - TimeSpan.FromSeconds(30);
            foreach (var run in runningRuns)
            {
                if (run.StartedAt >= cutoff)
                {
                    continue;
                }

                await runStore.SaveRunAsync(
                    run with
                    {
                        Status = "Failed",
                        FinishedAt = DateTimeOffset.UtcNow,
                        Response = "Agent run interrupted: the application restarted before the run finished."
                    },
                    CancellationToken.None);
                interrupted++;
            }

            if (interrupted > 0)
            {
                _logger.LogWarning(
                    "Marked {Count} interrupted agent run(s) as Failed on startup",
                    interrupted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile interrupted agent runs on startup");
        }
    }

    private async Task PersistTerminalStatusAsync(
        IAgentRunStore? runStore,
        Guid runId,
        string status,
        string response,
        bool isCancelled,
        bool overwriteFinished = false)
    {
        if (runStore is null)
        {
            return;
        }

        try
        {
            var existingRun = await runStore.GetRunAsync(runId, CancellationToken.None);
            if (existingRun is null ||
                (!overwriteFinished && existingRun.FinishedAt is not null))
            {
                return;
            }

            await runStore.SaveRunAsync(
                existingRun with
                {
                    Status = status,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Response = response,
                    IsCancelled = isCancelled
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {Status} status for agent run {RunId}", status, runId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.Complete();
        await base.StopAsync(cancellationToken);
    }
}