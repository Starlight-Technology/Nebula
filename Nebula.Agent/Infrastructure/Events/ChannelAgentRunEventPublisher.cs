using System.Collections.Concurrent;
using System.Threading.Channels;
using Nebula.Agent.Application.Events;

namespace Nebula.Agent.Infrastructure.Events;

public sealed class ChannelAgentRunEventPublisher : IAgentRunEventPublisher, IDisposable
{
    private const int MaxCompletedRunsToRetain = 128;
    private readonly Channel<AgentRunEvent> _globalChannel = Channel.CreateUnbounded<AgentRunEvent>();
    private readonly ConcurrentDictionary<Guid, List<Channel<AgentRunEvent>>> _runChannels = new();
    private readonly Dictionary<Guid, List<AgentRunEvent>> _runHistory = [];
    private readonly Queue<Guid> _completedRuns = new();
    private readonly object _lock = new();
    private bool _disposed;

    public void Publish(AgentRunEvent @event)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _globalChannel.Writer.TryWrite(@event);

            if (!_runHistory.TryGetValue(@event.RunId, out var history))
            {
                history = [];
                _runHistory.Add(@event.RunId, history);
            }

            history.Add(@event);

            if (_runChannels.TryGetValue(@event.RunId, out var channels))
            {
                foreach (var channel in channels)
                {
                    channel.Writer.TryWrite(@event);
                }
            }

            if (@event is AgentRunCompletedEvent or AgentRunFailedEvent or AgentRunCancelledEvent)
            {
                _completedRuns.Enqueue(@event.RunId);
                while (_completedRuns.Count > MaxCompletedRunsToRetain)
                {
                    _runHistory.Remove(_completedRuns.Dequeue());
                }
            }
        }
    }

    public IAsyncEnumerable<AgentRunEvent> Subscribe(Guid runId, CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<AgentRunEvent>();

        lock (_lock)
        {
            _runChannels.AddOrUpdate(runId,
                [channel],
                (_, list) => { list.Add(channel); return list; });

            if (_runHistory.TryGetValue(runId, out var history))
            {
                foreach (var @event in history)
                {
                    channel.Writer.TryWrite(@event);
                }
            }
        }

        return SubscribeImpl(channel.Reader.ReadAllAsync(cancellationToken), runId, channel);
    }

    private async IAsyncEnumerable<AgentRunEvent> SubscribeImpl(
        IAsyncEnumerable<AgentRunEvent> source,
        Guid runId,
        Channel<AgentRunEvent> channel)
    {
        try
        {
            await foreach (var evt in source.ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            lock (_lock)
            {
                if (_runChannels.TryGetValue(runId, out var list))
                {
                    list.Remove(channel);
                    if (list.Count == 0)
                    {
                        _runChannels.TryRemove(runId, out _);
                    }
                }
            }
            channel.Writer.Complete();
        }
    }

    public IAsyncEnumerable<AgentRunEvent> SubscribeAll(CancellationToken cancellationToken = default)
        => _globalChannel.Reader.ReadAllAsync(cancellationToken);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _globalChannel.Writer.Complete();
            foreach (var kvp in _runChannels)
            {
                foreach (var ch in kvp.Value)
                {
                    ch.Writer.Complete();
                }
            }

            _runChannels.Clear();
            _runHistory.Clear();
        }
    }
}