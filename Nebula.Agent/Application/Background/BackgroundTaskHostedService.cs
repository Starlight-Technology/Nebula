using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nebula.Core.Background;

namespace Nebula.Agent.Application.Background;

public sealed class BackgroundTaskHostedService(
    IBackgroundTaskQueue taskQueue,
    ILogger<BackgroundTaskHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Background task hosted service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var workItem = await taskQueue.DequeueAsync(stoppingToken);
            try
            {
                await workItem(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error executing background work item");
            }
        }

        logger.LogInformation("Background task hosted service stopped");
    }
}
