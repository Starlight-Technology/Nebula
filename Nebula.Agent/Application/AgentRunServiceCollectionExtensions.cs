using Microsoft.Extensions.DependencyInjection;

using Nebula.Agent.Application.Events;
using Nebula.Agent.Infrastructure.Events;
using Nebula.Agent.Infrastructure.Execution;

namespace Nebula.Agent.Application;

public static class AgentRunServiceCollectionExtensions
{
    public static IServiceCollection AddAgentRunProcessing(this IServiceCollection services)
    {
        services.AddSingleton<IAgentRunEventPublisher, ChannelAgentRunEventPublisher>();
        services.AddSingleton<AgentRunBackgroundService>();
        services.AddSingleton<IAgentRunQueue>(provider =>
            provider.GetRequiredService<AgentRunBackgroundService>());
        services.AddHostedService<AgentRunBackgroundService>(provider =>
            provider.GetRequiredService<AgentRunBackgroundService>());
        services.AddScoped<IAgentRunOrchestrator, AgentRunOrchestrator>();
        return services;
    }
}