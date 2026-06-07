using Nebula.Agent.Data;

namespace Nebula.Agent.Application;

public sealed record PreparedConversationContext(
    Guid ConversationId,
    string ModelPrompt,
    ConversationState? PreviousState);

public interface IConversationContextService
{
    Task<PreparedConversationContext> PrepareAsync(
        Guid conversationId,
        string prompt,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        PreparedConversationContext context,
        string prompt,
        ConversationTurn turn,
        CancellationToken cancellationToken);
}

public sealed class ConversationContextService(
    IConversationMemoryRepository? conversationRepository,
    NebulaContextBuilder contextBuilder,
    ILogger logger) : IConversationContextService
{
    private static readonly TimeSpan PersistenceTimeout = TimeSpan.FromMilliseconds(1500);

    public async Task<PreparedConversationContext> PrepareAsync(
        Guid conversationId,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (conversationRepository is null)
        {
            return new PreparedConversationContext(conversationId, prompt, PreviousState: null);
        }

        var userMessage = await AddMessageAsync(
            new ConversationMessage
            {
                ConversationId = conversationId,
                Role = ConversationRoles.User,
                Content = prompt.Trim()
            },
            cancellationToken);
        var recentMessages = await GetRecentMessagesAsync(conversationId, cancellationToken);
        var conversationState = await GetStateAsync(conversationId, cancellationToken);

        logger.Log(
            $"ConversationId '{conversationId}' loaded {recentMessages.Count} recent message(s). " +
            $"Conversation state: {(conversationState is null ? "missing" : "loaded")}.");

        return new PreparedConversationContext(
            conversationId,
            contextBuilder.Build(
                conversationId,
                conversationState,
                recentMessages,
                userMessage),
            conversationState);
    }

    public async Task CompleteAsync(
        PreparedConversationContext context,
        string prompt,
        ConversationTurn turn,
        CancellationToken cancellationToken)
    {
        if (conversationRepository is null)
        {
            return;
        }

        await AddMessageAsync(
            new ConversationMessage
            {
                ConversationId = context.ConversationId,
                Role = ConversationRoles.Assistant,
                Content = turn.Response
            },
            cancellationToken);
        await UpsertStateAsync(
            ConversationStateFactory.Create(
                context.ConversationId,
                context.PreviousState,
                prompt,
                turn),
            cancellationToken);
    }

    private async Task<ConversationMessage> AddMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            var savedMessage = await conversationRepository!.AddMessageAsync(message, timeout.Token);
            logger.Log(
                $"Saved {savedMessage.Role} conversation message '{savedMessage.Id}' " +
                $"for ConversationId '{savedMessage.ConversationId}'.");
            return savedMessage;
        }
        catch (OperationCanceledException)
        {
            LogCancellation(
                cancellationToken,
                $"Conversation message persistence for '{message.ConversationId}' was cancelled with the active conversation.",
                $"Timed out while persisting conversation message for ConversationId '{message.ConversationId}'.");
            return message;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"Unable to persist conversation message for ConversationId '{message.ConversationId}': {ex.Message}");
            return message;
        }
    }

    private async Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            return await conversationRepository!.GetRecentMessagesAsync(
                conversationId,
                contextBuilder.RecentMessageLimit,
                timeout.Token);
        }
        catch (OperationCanceledException)
        {
            LogCancellation(
                cancellationToken,
                $"Conversation history load for '{conversationId}' was cancelled with the active conversation.",
                $"Timed out while loading recent messages for ConversationId '{conversationId}'.");
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"Unable to load recent messages for ConversationId '{conversationId}': {ex.Message}");
            return [];
        }
    }

    private async Task<ConversationState?> GetStateAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            return await conversationRepository!.GetStateAsync(conversationId, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            LogCancellation(
                cancellationToken,
                $"Conversation state load for '{conversationId}' was cancelled with the active conversation.",
                $"Timed out while loading state for ConversationId '{conversationId}'.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"Unable to load state for ConversationId '{conversationId}': {ex.Message}");
            return null;
        }
    }

    private async Task UpsertStateAsync(
        ConversationState state,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            await conversationRepository!.UpsertStateAsync(state, timeout.Token);
            logger.Log($"Saved conversation state for ConversationId '{state.ConversationId}'.");
        }
        catch (OperationCanceledException)
        {
            LogCancellation(
                cancellationToken,
                $"Conversation state update for '{state.ConversationId}' was cancelled with the active conversation.",
                $"Timed out while saving state for ConversationId '{state.ConversationId}'.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"Unable to save state for ConversationId '{state.ConversationId}': {ex.Message}");
        }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PersistenceTimeout);
        return timeout;
    }

    private void LogCancellation(
        CancellationToken cancellationToken,
        string cancellationMessage,
        string timeoutMessage)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            logger.Log(cancellationMessage);
            return;
        }

        logger.LogError(timeoutMessage);
    }
}
