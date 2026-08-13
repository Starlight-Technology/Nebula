using Nebula.Agent.Data;
using Nebula.Core.Interactions;
using Nebula.Core.Learning;

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
        InteractionMode mode,
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
    ILogger logger,
    IKnowledgeQueryService? knowledgeQueryService = null) : IConversationContextService
{
    private static readonly TimeSpan PersistenceTimeout = TimeSpan.FromMilliseconds(1500);

    private const int MaxInjectedKnowledgeChars = 3000;

    public async Task<PreparedConversationContext> PrepareAsync(
        Guid conversationId,
        string prompt,
        InteractionMode mode,
        CancellationToken cancellationToken)
    {
        if (conversationRepository is null)
        {
            var currentMessage = new ConversationMessage
            {
                ConversationId = conversationId,
                Role = ConversationRoles.User,
                Content = prompt.Trim()
            };
            var noRepoModelPrompt = contextBuilder.Build(
                conversationId,
                state: null,
                [currentMessage],
                currentMessage,
                mode);
            return new PreparedConversationContext(
                conversationId,
                await AugmentWithKnowledgeAsync(noRepoModelPrompt, prompt, mode, cancellationToken),
                PreviousState: null);
        }

        var userMessage = await AddMessageAsync(
            new ConversationMessage
            {
                ConversationId = conversationId,
                Role = ConversationRoles.User,
                Content = prompt.Trim()
            },
            mode,
            cancellationToken);
        var recentMessages = await GetRecentMessagesAsync(conversationId, mode, cancellationToken);
        var conversationState = await GetStateAsync(conversationId, mode, cancellationToken);

        logger.Log(
            $"{ModePrefix(mode)} ConversationId '{conversationId}' loaded " +
            $"{recentMessages.Count} recent message(s). " +
            $"Conversation state: {(conversationState is null ? "missing" : "loaded")}.");

        var modelPrompt = contextBuilder.Build(
            conversationId,
            conversationState,
            recentMessages,
            userMessage,
            mode);
        return new PreparedConversationContext(
            conversationId,
            await AugmentWithKnowledgeAsync(modelPrompt, prompt, mode, cancellationToken),
            conversationState);
    }

    private async Task<string> AugmentWithKnowledgeAsync(
        string modelPrompt,
        string userPrompt,
        InteractionMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != InteractionMode.Chat || knowledgeQueryService is null)
        {
            return modelPrompt;
        }

        try
        {
            var knowledge = await knowledgeQueryService.AnswerAsync(
                userPrompt,
                cancellationToken);
            if (string.IsNullOrWhiteSpace(knowledge) ||
                knowledge.Contains("Nao ha conhecimento", StringComparison.OrdinalIgnoreCase) ||
                knowledge.Contains("Não há conhecimento", StringComparison.OrdinalIgnoreCase))
            {
                return modelPrompt;
            }

            if (knowledge.Length > MaxInjectedKnowledgeChars)
            {
                knowledge = knowledge[..MaxInjectedKnowledgeChars];
            }

            logger.Log(
                $"[CHAT] Injected {knowledge.Length} chars of learned knowledge for: {userPrompt}");
            return $"{modelPrompt}\n\n[knowledge]\n{knowledge}";
        }
        catch (Exception ex)
        {
            logger.Log(
                $"[CHAT] Knowledge augmentation failed (non-fatal): {ex.Message}");
            return modelPrompt;
        }
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
            turn.Mode,
            cancellationToken);
        await UpsertStateAsync(
            ConversationStateFactory.Create(
                context.ConversationId,
                context.PreviousState,
                prompt,
                turn),
            turn.Mode,
            cancellationToken);
    }

    private async Task<ConversationMessage> AddMessageAsync(
        ConversationMessage message,
        InteractionMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            var savedMessage = await conversationRepository!.AddMessageAsync(message, timeout.Token);
            logger.Log(
                $"{ModePrefix(mode)} Saved {savedMessage.Role} conversation message '{savedMessage.Id}' " +
                $"for ConversationId '{savedMessage.ConversationId}'.");
            return savedMessage;
        }
        catch (OperationCanceledException)
        {
            LogCancellation(
                cancellationToken,
                $"{ModePrefix(mode)} Conversation message persistence for '{message.ConversationId}' was cancelled with the active conversation.",
                $"{ModePrefix(mode)} Timed out while persisting conversation message for ConversationId '{message.ConversationId}'.");
            return message;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"{ModePrefix(mode)} Unable to persist conversation message for ConversationId '{message.ConversationId}': {ex.Message}");
            return message;
        }
    }

    private async Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(
        Guid conversationId,
        InteractionMode mode,
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
                $"{ModePrefix(mode)} Conversation history load for '{conversationId}' was cancelled with the active conversation.",
                $"{ModePrefix(mode)} Timed out while loading recent messages for ConversationId '{conversationId}'.");
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"{ModePrefix(mode)} Unable to load recent messages for ConversationId '{conversationId}': {ex.Message}");
            return [];
        }
    }

    private async Task<ConversationState?> GetStateAsync(
        Guid conversationId,
        InteractionMode mode,
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
                $"{ModePrefix(mode)} Conversation state load for '{conversationId}' was cancelled with the active conversation.",
                $"{ModePrefix(mode)} Timed out while loading state for ConversationId '{conversationId}'.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"{ModePrefix(mode)} Unable to load state for ConversationId '{conversationId}': {ex.Message}");
            return null;
        }
    }

    private async Task UpsertStateAsync(
        ConversationState state,
        InteractionMode mode,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            await conversationRepository!.UpsertStateAsync(state, timeout.Token);
            logger.Log(
                $"{ModePrefix(mode)} Saved conversation state for ConversationId '{state.ConversationId}'.");
        }
        catch (OperationCanceledException)
        {
            LogCancellation(
                cancellationToken,
                $"{ModePrefix(mode)} Conversation state update for '{state.ConversationId}' was cancelled with the active conversation.",
                $"{ModePrefix(mode)} Timed out while saving state for ConversationId '{state.ConversationId}'.");
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"{ModePrefix(mode)} Unable to save state for ConversationId '{state.ConversationId}': {ex.Message}");
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

    private static string ModePrefix(InteractionMode mode) =>
        mode == InteractionMode.Agent ? "[AGENT]" : "[CHAT]";
}
