using System.Collections.Generic;

namespace Nebula.Agent.Application.Events;

public interface IAgentRunEventPublisher
{
    void Publish(AgentRunEvent @event);
    IAsyncEnumerable<AgentRunEvent> Subscribe(Guid runId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentRunEvent> SubscribeAll(CancellationToken cancellationToken = default);
}