namespace Nebula.Agent.Data;

public class CompositeConversationMemoryRepository : IConversationMemoryRepository
{
    private readonly IReadOnlyList<IConversationMemoryStore> stores;
    private readonly ILogger? logger;

    public CompositeConversationMemoryRepository(
        IEnumerable<IConversationMemoryStore> stores,
        ILogger? logger = null)
    {
        this.stores = stores.ToList();
        this.logger = logger;
    }

    public async Task<ConversationMessage> AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        ConversationMessage? savedMessage = null;
        Exception? lastFailure = null;

        foreach (var store in stores)
        {
            try
            {
                var storeMessage = await store.AddMessageAsync(message, cancellationToken);
                savedMessage ??= storeMessage;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
                LogStoreFailure(store, "add a conversation message", ex);
            }
        }

        if (savedMessage is not null)
        {
            return savedMessage;
        }

        if (stores.Count == 0)
        {
            return message;
        }

        throw new InvalidOperationException("Unable to persist conversation message in any registered store.", lastFailure);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        var messages = new List<ConversationMessage>();

        foreach (var store in stores)
        {
            try
            {
                messages.AddRange(await store.GetRecentMessagesAsync(conversationId, limit, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogStoreFailure(store, "load recent conversation messages", ex);
            }
        }

        return messages
            .GroupBy(message => message.Id)
            .Select(group => group.First())
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(limit)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .ToList();
    }

    public async Task<ConversationState?> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var states = new List<ConversationState>();

        foreach (var store in stores)
        {
            try
            {
                var state = await store.GetStateAsync(conversationId, cancellationToken);
                if (state is not null)
                {
                    states.Add(state);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogStoreFailure(store, "load conversation state", ex);
            }
        }

        return states
            .OrderByDescending(state => state.UpdatedAt)
            .FirstOrDefault();
    }

    public async Task<ConversationState> UpsertStateAsync(ConversationState state, CancellationToken cancellationToken = default)
    {
        ConversationState? savedState = null;
        Exception? lastFailure = null;

        foreach (var store in stores)
        {
            try
            {
                var storeState = await store.UpsertStateAsync(state, cancellationToken);
                savedState ??= storeState;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastFailure = ex;
                LogStoreFailure(store, "save conversation state", ex);
            }
        }

        if (savedState is not null)
        {
            return savedState;
        }

        if (stores.Count == 0)
        {
            return state;
        }

        throw new InvalidOperationException("Unable to persist conversation state in any registered store.", lastFailure);
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetRecentConversationsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        var summaries = new List<ConversationSummary>();

        foreach (var store in stores)
        {
            try
            {
                var storeSummaries = await store.GetRecentConversationsAsync(limit, cancellationToken);
                if (storeSummaries is not null)
                {
                    summaries.AddRange(storeSummaries);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogStoreFailure(store, "load recent conversations", ex);
            }
        }

        return summaries
            .GroupBy(summary => summary.ConversationId)
            .Select(group => group.OrderByDescending(summary => summary.UpdatedAt).First())
            .OrderByDescending(summary => summary.UpdatedAt)
            .Take(limit)
            .ToList();
    }

    private void LogStoreFailure(
        IConversationMemoryStore store,
        string operation,
        Exception exception)
    {
        logger?.LogError(
            $"Conversation store '{store.GetType().Name}' failed to {operation}: {exception.Message}");
    }
}
