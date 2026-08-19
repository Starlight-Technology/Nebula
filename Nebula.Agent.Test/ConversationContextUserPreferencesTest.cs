using Moq;

using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Core.Interactions;
using Nebula.Core.Memory;
using Nebula.Services.Memory;

namespace Nebula.Agent.Test;

public sealed class ConversationContextUserPreferencesTest
{
    [Fact]
    public async Task prepare_must_inject_user_preferences_in_chat_mode()
    {
        var store = new InMemoryUserMemoryStore();
        var userMemory = new UserMemoryService(store, new Mock<ILogger>().Object);
        await userMemory.SetPreferenceAsync(
            "default",
            UserMemoryKind.Language,
            "Language",
            "pt-BR");

        var contextService = new ConversationContextService(
            conversationRepository: null,
            new NebulaContextBuilder(),
            new Mock<ILogger>().Object,
            userMemoryService: userMemory);
        var context = await contextService.PrepareAsync(
            Guid.NewGuid(),
            "Qual e a capital do Brasil?",
            InteractionMode.Chat,
            CancellationToken.None);

        Assert.Contains("[user_preferences]", context.ModelPrompt, StringComparison.Ordinal);
        Assert.Contains("Idioma: pt-BR", context.ModelPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task prepare_must_not_inject_preferences_in_agent_mode()
    {
        var store = new InMemoryUserMemoryStore();
        var userMemory = new UserMemoryService(store, new Mock<ILogger>().Object);
        await userMemory.SetPreferenceAsync(
            "default",
            UserMemoryKind.Style,
            "Style",
            "direto");

        var contextService = new ConversationContextService(
            conversationRepository: null,
            new NebulaContextBuilder(),
            new Mock<ILogger>().Object,
            userMemoryService: userMemory);
        var context = await contextService.PrepareAsync(
            Guid.NewGuid(),
            "Crie o projeto",
            InteractionMode.Agent,
            CancellationToken.None);

        Assert.DoesNotContain("[user_preferences]", context.ModelPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task prepare_must_skip_injection_when_no_preferences_saved()
    {
        var contextService = new ConversationContextService(
            conversationRepository: null,
            new NebulaContextBuilder(),
            new Mock<ILogger>().Object,
            userMemoryService: new UserMemoryService(
                new InMemoryUserMemoryStore(),
                new Mock<ILogger>().Object));
        var context = await contextService.PrepareAsync(
            Guid.NewGuid(),
            "Ola",
            InteractionMode.Chat,
            CancellationToken.None);

        Assert.DoesNotContain("[user_preferences]", context.ModelPrompt, StringComparison.Ordinal);
    }
}