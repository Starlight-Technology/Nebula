using System.Text;

namespace Nebula.Agent.Data;

public sealed record ConversationSummary(
    Guid ConversationId,
    string Title,
    DateTime UpdatedAt,
    int MessageCount)
{
    public static ConversationSummary FromState(ConversationState state, int messageCount)
    {
        return new ConversationSummary(
            state.ConversationId,
            DeriveTitle(state),
            state.UpdatedAt,
            messageCount);
    }

    public static string DeriveTitle(ConversationState state)
    {
        if (!string.IsNullOrWhiteSpace(state.CurrentGoal))
        {
            return TruncateTitle(FirstLine(state.CurrentGoal));
        }

        if (!string.IsNullOrWhiteSpace(state.Summary))
        {
            var userLine = state.Summary
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("User:", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(userLine))
            {
                return TruncateTitle(userLine[5..].Trim());
            }

            return TruncateTitle(FirstLine(state.Summary));
        }

        return "Conversa";
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length == 0 ? "Conversa" : line;
    }

    private static string TruncateTitle(string text)
    {
        const int maxLength = 70;
        return text.Length <= maxLength ? text : $"{text[..(maxLength - 1)]}\u2026";
    }
}
