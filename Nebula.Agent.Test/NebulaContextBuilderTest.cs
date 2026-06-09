using Nebula.Agent.Data;

namespace Nebula.Agent.Test;

public sealed class NebulaContextBuilderTest
{
    [Fact]
    public void build_must_keep_only_the_configured_number_of_recent_messages()
    {
        var conversationId = Guid.NewGuid();
        var messages = new[]
        {
            CreateMessage(conversationId, ConversationRoles.System, "System note", 1),
            CreateMessage(conversationId, ConversationRoles.User, "First question", 2),
            CreateMessage(conversationId, ConversationRoles.Assistant, "First answer", 3),
            CreateMessage(conversationId, ConversationRoles.User, "Current question", 4)
        };
        var builder = new NebulaContextBuilder(new ConversationContextOptions
        {
            MaxRecentMessages = 2,
            MaxApproximateHistoryTokens = 1000
        });

        var context = builder.Build(
            conversationId,
            state: null,
            messages,
            messages[^1]);

        Assert.DoesNotContain("System note", context);
        Assert.Contains("user: First question", context);
        Assert.Contains("assistant: First answer", context);
        Assert.Contains("Current question", context);
    }

    [Fact]
    public void build_must_respect_the_history_token_budget()
    {
        var conversationId = Guid.NewGuid();
        var oldMessage = CreateMessage(
            conversationId,
            ConversationRoles.User,
            new string('a', 80),
            1);
        var recentMessage = CreateMessage(
            conversationId,
            ConversationRoles.Assistant,
            "short answer",
            2);
        var currentMessage = CreateMessage(
            conversationId,
            ConversationRoles.User,
            "continue",
            3);
        var builder = new NebulaContextBuilder(new ConversationContextOptions
        {
            MaxRecentMessages = 10,
            MaxApproximateHistoryTokens = 12
        });

        var context = builder.Build(
            conversationId,
            state: null,
            [oldMessage, recentMessage, currentMessage],
            currentMessage);

        Assert.DoesNotContain(oldMessage.Content, context);
        Assert.Contains("assistant: short answer", context);
    }

    [Fact]
    public void build_must_preserve_supported_conversation_roles()
    {
        var conversationId = Guid.NewGuid();
        var systemMessage = CreateMessage(
            conversationId,
            ConversationRoles.System,
            "Use concise answers",
            1);
        var toolMessage = CreateMessage(
            conversationId,
            ConversationRoles.Tool,
            "command output",
            2);
        var currentMessage = CreateMessage(
            conversationId,
            ConversationRoles.User,
            "summarize",
            3);
        var builder = new NebulaContextBuilder();

        var context = builder.Build(
            conversationId,
            state: null,
            [systemMessage, toolMessage, currentMessage],
            currentMessage);

        Assert.Contains("system: Use concise answers", context);
        Assert.Contains("tool: command output", context);
        Assert.Contains("[current_user_message]", context);
        Assert.Contains("summarize", context);
    }

    private static ConversationMessage CreateMessage(
        Guid conversationId,
        string role,
        string content,
        int order)
    {
        return new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = role,
            Content = content,
            CreatedAt = DateTime.UnixEpoch.AddMinutes(order)
        };
    }
}
