using Microsoft.EntityFrameworkCore;

using Nebula.Agent.Data;
using Nebula.Postgres.Context;

namespace Nebula.Agent.Test;

public sealed class PostgresConversationMemoryRepositoryTest
{
    [Fact]
    public async Task get_recent_conversations_must_return_states_ordered_by_updated_at()
    {
        await using var context = CreateContext();
        var repository = new PostgresConversationMemoryRepository(context);
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
        Assert.Equal("newer goal", conversations[0].Title);
        Assert.Equal(olderId, conversations[1].ConversationId);
    }

    [Fact]
    public async Task get_recent_conversations_must_count_messages()
    {
        await using var context = CreateContext();
        var repository = new PostgresConversationMemoryRepository(context);
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
    public async Task get_recent_conversations_must_respect_limit()
    {
        await using var context = CreateContext();
        var repository = new PostgresConversationMemoryRepository(context);

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

    private static PostgresContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PostgresContext>()
            .UseInMemoryDatabase($"nebula-conversations-{Guid.NewGuid():N}")
            .Options;
        return new PostgresContext(options);
    }
}
