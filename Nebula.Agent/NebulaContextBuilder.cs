using System.Text;

using Nebula.Agent.Data;
using Nebula.Core.Configuration;
using Nebula.Core.Interactions;

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

    private const string SharedSystemPrompt = """
        You are Nebula, a local assistant.
        Use the conversation history to resolve references such as "it", "that", "ela", "isso", and "a anterior".
        Keep continuity across turns, but follow the current user message as the immediate instruction.
        """;

    private const string ChatModePrompt = """
        Você está em CHAT MODE.

        Seu trabalho é apenas responder perguntas e conversar.

        Você não deve executar tarefas.
        Você não deve criar planos de execução.
        Você não deve chamar ferramentas.
        Você não deve acessar arquivos.

        Responda apenas em linguagem natural.
        """;

    private const string AgentModePrompt = """
        Você está em AGENT MODE.

        O usuário espera que a tarefa seja executada.

        Não responda apenas com explicações.
        Crie um plano.
        Execute as etapas.
        Colete evidências.
        Relate somente resultados observados.

        Se algo não puder ser executado, informe claramente.
        Não invente resultados.
        Não afirme nada sem evidência.
        """;

    private readonly ConversationContextOptions options;
    private readonly NebulaRuntimeSettings runtimeSettings;

    public NebulaContextBuilder(
        ConversationContextOptions? options = null,
        NebulaRuntimeSettings? runtimeSettings = null)
    {
        this.options = options ?? new ConversationContextOptions();
        this.runtimeSettings = runtimeSettings ?? new NebulaRuntimeSettings();
    }

    public int RecentMessageLimit => Math.Max(1, options.MaxRecentMessages);

    public string Build(
        Guid conversationId,
        ConversationState? state,
        IReadOnlyList<ConversationMessage> recentMessages,
        ConversationMessage currentUserMessage,
        InteractionMode mode)
    {
        var context = new StringBuilder();

        AppendSystemPrompt(context, mode);
        AppendConversationHeader(context, conversationId, mode);
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

    private void AppendSystemPrompt(
        StringBuilder context,
        InteractionMode mode)
    {
        context.AppendLine("[system]");
        context.AppendLine(SharedSystemPrompt.Trim());
        context.AppendLine();
        context.AppendLine(
            mode == InteractionMode.Agent
                ? AgentModePrompt.Trim()
                : ChatModePrompt.Trim());
        context.AppendLine();
        context.AppendLine(runtimeSettings.BuildResponseLanguageInstruction());
        context.AppendLine();
    }

    private static void AppendConversationHeader(
        StringBuilder context,
        Guid conversationId,
        InteractionMode mode)
    {
        context.AppendLine("[conversation]");
        context.AppendLine($"ConversationId: {conversationId}");
        context.AppendLine($"InteractionMode: {mode}");
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
