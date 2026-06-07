using System.Text;

using Nebula.Agent.Data;

namespace Nebula.Agent;

public sealed class ConversationContextOptions
{
    public const int DefaultRecentMessageLimit = 12;
    public const int DefaultApproximateHistoryTokenLimit = 4096;

    public int MaxRecentMessages { get; init; } = DefaultRecentMessageLimit;

    public int MaxApproximateHistoryTokens { get; init; } = DefaultApproximateHistoryTokenLimit;
}

public class NebulaContextBuilder
{
    public const int DefaultRecentMessageLimit = ConversationContextOptions.DefaultRecentMessageLimit;

    private const string SystemPrompt = """
        You are Nebula, a local assistant that helps the user through conversation and safe local actions.
        Use the conversation history to resolve references such as "it", "that", "ela", "isso", and "a anterior".
        Keep continuity across turns, but follow the current user message as the immediate instruction.
        If action execution is needed, plan only the steps required for the current request.
        """;

    private readonly ConversationContextOptions options;

    public NebulaContextBuilder(ConversationContextOptions? options = null)
    {
        this.options = options ?? new ConversationContextOptions();
    }

    public int RecentMessageLimit => Math.Max(1, options.MaxRecentMessages);

    public string Build(
        Guid conversationId,
        ConversationState? state,
        IReadOnlyList<ConversationMessage> recentMessages,
        ConversationMessage currentUserMessage)
    {
        var context = new StringBuilder();

        AppendSystemPrompt(context);
        AppendConversationHeader(context, conversationId);
        AppendConversationState(context, state);
        AppendRecentMessages(context, SelectHistory(recentMessages, currentUserMessage));
        AppendCurrentMessage(context, currentUserMessage);

        return context.ToString().Trim();
    }

    private IReadOnlyList<ConversationMessage> SelectHistory(
        IReadOnlyList<ConversationMessage> recentMessages,
        ConversationMessage currentUserMessage)
    {
        var candidates = recentMessages
            .Where(message => message.Id != currentUserMessage.Id)
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .TakeLast(RecentMessageLimit)
            .ToList();
        var tokenBudget = Math.Max(1, options.MaxApproximateHistoryTokens);
        var selected = new List<ConversationMessage>();

        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            var message = candidates[index];
            var messageCost = EstimateTokenCount(message);
            if (messageCost > tokenBudget)
            {
                break;
            }

            selected.Add(message);
            tokenBudget -= messageCost;
        }

        selected.Reverse();
        return selected;
    }

    private static void AppendSystemPrompt(StringBuilder context)
    {
        context.AppendLine("[system]");
        context.AppendLine(SystemPrompt.Trim());
        context.AppendLine();
    }

    private static void AppendConversationHeader(StringBuilder context, Guid conversationId)
    {
        context.AppendLine("[conversation]");
        context.AppendLine($"ConversationId: {conversationId}");
        context.AppendLine();
    }

    private static void AppendConversationState(
        StringBuilder context,
        ConversationState? state)
    {
        if (!HasStateContent(state))
        {
            return;
        }

        context.AppendLine("[conversation_state]");
        AppendStateValue(context, "Summary", state!.Summary);
        AppendStateValue(context, "CurrentGoal", state.CurrentGoal);
        AppendStateValue(context, "CurrentPlan", state.CurrentPlan);
        context.AppendLine();
    }

    private static void AppendRecentMessages(
        StringBuilder context,
        IReadOnlyList<ConversationMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        context.AppendLine("[recent_messages]");
        foreach (var message in messages)
        {
            context.AppendLine($"{NormalizeRole(message.Role)}: {message.Content.Trim()}");
        }

        context.AppendLine();
    }

    private static void AppendCurrentMessage(
        StringBuilder context,
        ConversationMessage currentUserMessage)
    {
        context.AppendLine("[current_user_message]");
        context.AppendLine(currentUserMessage.Content.Trim());
    }

    private static bool HasStateContent(ConversationState? state)
    {
        return state is not null &&
               (!string.IsNullOrWhiteSpace(state.Summary) ||
                !string.IsNullOrWhiteSpace(state.CurrentGoal) ||
                !string.IsNullOrWhiteSpace(state.CurrentPlan));
    }

    private static void AppendStateValue(
        StringBuilder context,
        string label,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            context.AppendLine($"{label}: {value.Trim()}");
        }
    }

    private static int EstimateTokenCount(ConversationMessage message)
    {
        const int messageOverheadTokens = 4;
        var characterCount = message.Role.Length + message.Content.Length;
        return messageOverheadTokens + Math.Max(1, (characterCount + 3) / 4);
    }

    private static string NormalizeRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            ConversationRoles.System => ConversationRoles.System,
            ConversationRoles.User => ConversationRoles.User,
            ConversationRoles.Assistant => ConversationRoles.Assistant,
            ConversationRoles.Tool => ConversationRoles.Tool,
            _ => ConversationRoles.User
        };
    }
}
