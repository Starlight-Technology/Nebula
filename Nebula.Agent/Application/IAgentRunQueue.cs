using System.Threading.Channels;

namespace Nebula.Agent.Application;

public sealed record AgentRunWorkItem(
    Guid RunId,
    AgentActionRunRequest Request,
    CancellationToken CancellationToken
);

public interface IAgentRunQueue
{
    ValueTask EnqueueAsync(AgentRunWorkItem workItem, CancellationToken cancellationToken = default);
}