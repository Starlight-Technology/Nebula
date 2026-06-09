using Moq;

using Nebula.Agent.Data;

namespace Nebula.Agent.Test;

public sealed class CompositeConversationMemoryRepositoryTest
{
    [Fact]
    public async Task get_recent_messages_must_log_store_failure_and_use_available_store()
    {
        var conversationId = Guid.NewGuid();
        var expectedMessage = new ConversationMessage
        {
            ConversationId = conversationId,
            Role = ConversationRoles.User,
            Content = "hello"
        };
        var failingStore = new Mock<IConversationMemoryStore>();
        failingStore
            .Setup(store => store.GetRecentMessagesAsync(
                conversationId,
                10,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));
        var availableStore = new Mock<IConversationMemoryStore>();
        availableStore
            .Setup(store => store.GetRecentMessagesAsync(
                conversationId,
                10,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([expectedMessage]);
        var logger = new Mock<ILogger>();
        var repository = new CompositeConversationMemoryRepository(
            [failingStore.Object, availableStore.Object],
            logger.Object);

        var messages = await repository.GetRecentMessagesAsync(conversationId, 10);

        Assert.Single(messages);
        Assert.Same(expectedMessage, messages[0]);
        logger.Verify(
            currentLogger => currentLogger.LogError(
                It.Is<string>(message =>
                    message.Contains("store unavailable", StringComparison.Ordinal))),
            Times.Once);
    }
}
