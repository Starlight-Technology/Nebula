using Moq;

using Nebula.Agent.Data;

namespace Nebula.Agent.Test;

public sealed class ConversationMemoryRepositoryTest
{
    [Fact]
    public async Task in_memory_get_recent_conversations_must_order_by_updated_at_desc()
    {
        var repository = new InMemoryConversationMemoryRepository();
        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();

        await repository.UpsertStateAsync(new ConversationState
        {
            ConversationId = olderId,
            CurrentGoal = "older goal",
            UpdatedAt = DateTime.UtcNow.AddHours(-2)
        });
        await repository.UpsertStateAsync(new ConversationState
        {
            ConversationId = newerId,
            CurrentGoal = "newer goal",
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        });

        var conversations = await repository.GetRecentConversationsAsync(10);

        Assert.Equal(2, conversations.Count);
        Assert.Equal(newerId, conversations[0].ConversationId);
        Assert.Equal(olderId, conversations[1].ConversationId);
    }

    [Fact]
    public async Task in_memory_get_recent_conversations_must_apply_limit()
    {
        var repository = new InMemoryConversationMemoryRepository();

        for (var index = 0; index < 5; index++)
        {
            await repository.UpsertStateAsync(new ConversationState
            {
                ConversationId = Guid.NewGuid(),
                CurrentGoal = $"goal {index}",
                UpdatedAt = DateTime.UtcNow.AddMinutes(index)
            });
        }

        var conversations = await repository.GetRecentConversationsAsync(2);

        Assert.Equal(2, conversations.Count);
    }

    [Fact]
    public async Task in_memory_get_recent_conversations_must_derive_title_from_current_goal()
    {
        var repository = new InMemoryConversationMemoryRepository();
        var conversationId = Guid.NewGuid();

        await repository.UpsertStateAsync(new ConversationState
        {
            ConversationId = conversationId,
            CurrentGoal = "Refatorar o modulo de seguranca",
            UpdatedAt = DateTime.UtcNow
        });

        var conversations = await repository.GetRecentConversationsAsync(10);

        var summary = Assert.Single(conversations);
        Assert.Equal("Refatorar o modulo de seguranca", summary.Title);
    }

    [Fact]
    public async Task in_memory_get_recent_conversations_must_fall_back_to_user_summary_line()
    {
        var repository = new InMemoryConversationMemoryRepository();
        var conversationId = Guid.NewGuid();

        await repository.UpsertStateAsync(new ConversationState
        {
            ConversationId = conversationId,
            Summary = "User: primeira pergunta\nAssistant: resposta",
            CurrentGoal = null,
            UpdatedAt = DateTime.UtcNow
        });

        var conversations = await repository.GetRecentConversationsAsync(10);

        var summary = Assert.Single(conversations);
        Assert.Equal("primeira pergunta", summary.Title);
    }

    [Fact]
    public async Task in_memory_get_recent_conversations_must_count_messages_per_conversation()
    {
        var repository = new InMemoryConversationMemoryRepository();
        var conversationId = Guid.NewGuid();

        await repository.AddMessageAsync(new ConversationMessage
        {
            ConversationId = conversationId,
            Role = ConversationRoles.User,
            Content = "hello"
        });
        await repository.AddMessageAsync(new ConversationMessage
        {
            ConversationId = conversationId,
            Role = ConversationRoles.Assistant,
            Content = "hi"
        });
        await repository.UpsertStateAsync(new ConversationState
        {
            ConversationId = conversationId,
            CurrentGoal = "goal",
            UpdatedAt = DateTime.UtcNow
        });

        var conversations = await repository.GetRecentConversationsAsync(10);

        var summary = Assert.Single(conversations);
        Assert.Equal(2, summary.MessageCount);
    }

    [Fact]
    public async Task in_memory_get_recent_conversations_must_return_empty_when_no_states_exist()
    {
        var repository = new InMemoryConversationMemoryRepository();

        var conversations = await repository.GetRecentConversationsAsync(10);

        Assert.Empty(conversations);
    }

    [Fact]
    public async Task composite_get_recent_conversations_must_merge_and_dedupe_by_conversation_id()
    {
        var conversationId = Guid.NewGuid();
        var firstStore = new Mock<IConversationMemoryStore>();
        firstStore
            .Setup(store => store.GetRecentConversationsAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ConversationSummary(
                    conversationId,
                    "titulo antigo",
                    DateTime.UtcNow.AddHours(-3),
                    1)
            ]);
        var secondStore = new Mock<IConversationMemoryStore>();
        secondStore
            .Setup(store => store.GetRecentConversationsAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ConversationSummary(
                    conversationId,
                    "titulo recente",
                    DateTime.UtcNow.AddHours(-1),
                    5),
                new ConversationSummary(
                    Guid.NewGuid(),
                    "outra conversa",
                    DateTime.UtcNow,
                    2)
            ]);
        var repository = new CompositeConversationMemoryRepository([firstStore.Object, secondStore.Object]);

        var conversations = await repository.GetRecentConversationsAsync(10);

        Assert.Equal(2, conversations.Count);
        Assert.Contains(conversations, summary => summary.ConversationId == conversationId && summary.Title == "titulo recente");
    }

    [Fact]
    public async Task composite_get_recent_conversations_must_ignore_store_failure()
    {
        var failingStore = new Mock<IConversationMemoryStore>();
        failingStore
            .Setup(store => store.GetRecentConversationsAsync(10, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));
        var logger = new Mock<ILogger>();
        var repository = new CompositeConversationMemoryRepository([failingStore.Object], logger.Object);

        var conversations = await repository.GetRecentConversationsAsync(10);

        Assert.Empty(conversations);
        logger.Verify(
            currentLogger => currentLogger.LogError(
                It.Is<string>(message =>
                    message.Contains("store unavailable", StringComparison.Ordinal))),
            Times.Once);
    }
}
