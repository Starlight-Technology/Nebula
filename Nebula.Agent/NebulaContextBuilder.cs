using System.Text;

using Nebula.Agent.Data;

namespace Nebula.Agent;

public class NebulaContextBuilder
{
    public const int DefaultRecentMessageLimit = 12;

    private const string SystemPrompt = """
        You are Nebula, a local assistant that helps the user through conversation and safe local actions.
        Use the conversation history to resolve references such as "it", "that", "ela", "isso", and "a anterior".
        Keep continuity across turns, but follow the current user message as the immediate instruction.
        If action execution is needed, plan only the steps required for the current request.
        """;

    public string Build(
        Guid conversationId,
        ConversationState? state,
        IReadOnlyList<ConversationMessage> recentMessages,
        ConversationMessage currentUserMessage)
    {
        var builder = new StringBuilder();

        builder.AppendLine("[system]");
        builder.AppendLine(SystemPrompt.Trim());
        builder.AppendLine();

        builder.AppendLine("[conversation]");
        builder.AppendLine($"ConversationId: {conversationId}");
        builder.AppendLine();

        if (state is not null &&
            (!string.IsNullOrWhiteSpace(state.Summary) ||
             !string.IsNullOrWhiteSpace(state.CurrentGoal) ||
             !string.IsNullOrWhiteSpace(state.CurrentPlan)))
        {
            builder.AppendLine("[conversation_state]");

            if (!string.IsNullOrWhiteSpace(state.Summary))
            {
                builder.AppendLine($"Summary: {state.Summary.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(state.CurrentGoal))
            {
                builder.AppendLine($"CurrentGoal: {state.CurrentGoal.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(state.CurrentPlan))
            {
                builder.AppendLine($"CurrentPlan: {state.CurrentPlan.Trim()}");
            }

            builder.AppendLine();
        }

        var previousMessages = recentMessages
            .Where(message => message.Id != currentUserMessage.Id)
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .ToList();

        if (previousMessages.Count > 0)
        {
            builder.AppendLine("[recent_messages]");

            foreach (var message in previousMessages)
            {
                builder.AppendLine($"{NormalizeRole(message.Role)}: {message.Content.Trim()}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("[current_user_message]");
        builder.AppendLine(currentUserMessage.Content.Trim());

        return builder.ToString().Trim();
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
