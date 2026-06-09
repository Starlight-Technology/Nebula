using System.Text;

using Nebula.Agent.Data;

namespace Nebula.Agent.Application;

internal static class ConversationStateFactory
{
    public static ConversationState Create(
        Guid conversationId,
        ConversationState? previousState,
        string prompt,
        ConversationTurn turn)
    {
        return new ConversationState
        {
            ConversationId = conversationId,
            Summary = BuildSummary(previousState?.Summary, prompt, turn.Response),
            CurrentGoal = TextTruncation.Truncate(prompt.Trim(), 1000),
            CurrentPlan = BuildCurrentPlan(turn) ?? previousState?.CurrentPlan,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static string BuildSummary(string? previousSummary, string prompt, string response)
    {
        var summary = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            summary.AppendLine(previousSummary.Trim());
        }

        summary.AppendLine($"User: {TextTruncation.Truncate(prompt.Trim(), 500)}");
        summary.AppendLine($"Assistant: {TextTruncation.Truncate(response.Trim(), 500)}");

        return TextTruncation.TruncateFromStart(summary.ToString().Trim(), 4000);
    }

    private static string? BuildCurrentPlan(ConversationTurn turn)
    {
        if (turn.Commands.Count == 0)
        {
            return null;
        }

        var plan = new StringBuilder();
        foreach (var command in turn.Commands)
        {
            plan.AppendLine(
                $"{command.Id}. {command.Objective} - {GetStatus(command)}" +
                $"{(command.Required ? " - obrigatorio" : " - opcional")}");
        }

        return TextTruncation.Truncate(plan.ToString().Trim(), 2000);
    }

    private static string GetStatus(CommandExecution command)
    {
        if (command.Skipped)
        {
            return "nao executado por dependencia";
        }

        return command.Executed ? "executado" : "bloqueado";
    }
}
