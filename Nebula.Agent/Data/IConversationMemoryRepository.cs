namespace Nebula.Agent.Data;

public interface IConversationMemoryRepository
{
    Task<ConversationMessage> AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ConversationState?> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationState> UpsertStateAsync(ConversationState state, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationSummary>> GetRecentConversationsAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
