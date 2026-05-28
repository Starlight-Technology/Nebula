namespace Nebula.Agent.Data;

public class InMemoryConversationMemoryRepository : IConversationMemoryStore
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, List<ConversationMessage>> messagesByConversationId = [];
    private readonly Dictionary<Guid, ConversationState> statesByConversationId = [];

    public Task<ConversationMessage> AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var savedMessage = Clone(message);
        savedMessage.Id = savedMessage.Id == Guid.Empty ? Guid.NewGuid() : savedMessage.Id;
        savedMessage.CreatedAt = savedMessage.CreatedAt == default ? DateTime.UtcNow : savedMessage.CreatedAt;

        lock (gate)
        {
            if (!messagesByConversationId.TryGetValue(savedMessage.ConversationId, out var messages))
            {
                messages = [];
                messagesByConversationId[savedMessage.ConversationId] = messages;
            }

            messages.Add(Clone(savedMessage));
        }

        return Task.FromResult(savedMessage);
    }

    public Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);
        }

        lock (gate)
        {
            if (!messagesByConversationId.TryGetValue(conversationId, out var messages))
            {
                return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);
            }

            IReadOnlyList<ConversationMessage> result = messages
                .OrderByDescending(message => message.CreatedAt)
                .ThenByDescending(message => message.Id)
                .Take(limit)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(Clone)
                .ToList();

            return Task.FromResult(result);
        }
    }

    public Task<ConversationState?> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            return Task.FromResult(
                statesByConversationId.TryGetValue(conversationId, out var state)
                    ? Clone(state)
                    : null);
        }
    }

    public Task<ConversationState> UpsertStateAsync(ConversationState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var savedState = Clone(state);
        savedState.UpdatedAt = savedState.UpdatedAt == default ? DateTime.UtcNow : savedState.UpdatedAt;

        lock (gate)
        {
            statesByConversationId[savedState.ConversationId] = Clone(savedState);
        }

        return Task.FromResult(savedState);
    }

    private static ConversationMessage Clone(ConversationMessage message)
    {
        return new ConversationMessage
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            Role = message.Role,
            Content = message.Content,
            CreatedAt = message.CreatedAt
        };
    }

    private static ConversationState Clone(ConversationState state)
    {
        return new ConversationState
        {
            ConversationId = state.ConversationId,
            Summary = state.Summary,
            CurrentGoal = state.CurrentGoal,
            CurrentPlan = state.CurrentPlan,
            UpdatedAt = state.UpdatedAt
        };
    }
}
