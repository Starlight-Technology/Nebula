using Moq;

using Nebula.Agent;
using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Core.Interactions;
using Nebula.Core.Learning;
using Nebula.Llama.Client;
using Nebula.Services.Learning;

namespace Nebula.Agent.Test.Learning;

public sealed class KnowledgeAutomationLearningTest
{
    [Fact]
    public async Task AnswerForAutomationAsync_returns_no_knowledge_when_item_fails_policy()
    {
        var store = new InMemoryKnowledgeStore();
        var item = CreateItem(
            title: "Dangerous rm command",
            finalScore: 0.95,
            isDangerous: true,
            riskLevel: KnowledgeRiskLevel.Dangerous);
        await store.SaveAsync(
            item,
            [],
            [],
            new KnowledgeExperiment { KnowledgeItemId = item.Id });

        var queryService = new KnowledgeQueryService(
            store,
            new Mock<ILogger>().Object,
            automationPolicy: new KnowledgeAutomationPolicy());

        var answer = await queryService.AnswerForAutomationAsync("rm", CancellationToken.None);

        Assert.Contains("conhecimento armazenado", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnswerForAutomationAsync_injects_trusted_item()
    {
        var store = new InMemoryKnowledgeStore();
        var item = CreateItem(
            title: "dotnet build command",
            finalScore: 0.90,
            isDangerous: false,
            riskLevel: KnowledgeRiskLevel.Safe);
        item.Content = "Run 'dotnet build' after changing project files.";
        item.Summary = "Builds the solution with dotnet build.";
        await store.SaveAsync(
            item,
            [],
            [],
            new KnowledgeExperiment { KnowledgeItemId = item.Id });

        var queryService = new KnowledgeQueryService(
            store,
            new Mock<ILogger>().Object,
            automationPolicy: new KnowledgeAutomationPolicy());

        var answer = await queryService.AnswerForAutomationAsync("dotnet build", CancellationToken.None);

        Assert.Contains("dotnet build", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Não há conhecimento", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnswerForAutomationAsync_without_policy_returns_any_item()
    {
        var store = new InMemoryKnowledgeStore();
        var item = CreateItem(
            title: "rm -rf redirect trick",
            finalScore: 0.95,
            isDangerous: true,
            riskLevel: KnowledgeRiskLevel.HighRisk);
        await store.SaveAsync(
            item,
            [],
            [],
            new KnowledgeExperiment { KnowledgeItemId = item.Id });

        var queryService = new KnowledgeQueryService(
            store,
            new Mock<ILogger>().Object);

        var answer = await queryService.AnswerForAutomationAsync("rm", CancellationToken.None);

        Assert.Contains("rm", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Não há conhecimento", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chat_context_is_augmented_with_knowledge_when_available()
    {
        var store = new InMemoryKnowledgeStore();
        var item = CreateItem(
            title: "O que é o Nebula",
            finalScore: 0.90,
            isDangerous: false,
            riskLevel: KnowledgeRiskLevel.Safe);
        item.Content = "O Nebula é um assistente local-first e agente com LLM.";
        item.Summary = "Nebula é um assistente pessoal local-first.";
        await store.SaveAsync(
            item,
            [],
            [],
            new KnowledgeExperiment { KnowledgeItemId = item.Id });

        var contextService = CreateContextService(store);

        var prepared = await contextService.PrepareAsync(
            Guid.NewGuid(),
            "Nebula",
            InteractionMode.Chat,
            CancellationToken.None);

        Assert.True(
            prepared.ModelPrompt.Contains("[knowledge]", StringComparison.Ordinal),
            $"ModelPrompt did not contain [knowledge]. Actual:\n{prepared.ModelPrompt}");
        Assert.Contains("Nebula", prepared.ModelPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_context_is_not_augmented_when_no_knowledge_exists()
    {
        var store = new InMemoryKnowledgeStore();
        var contextService = CreateContextService(store);

        var prepared = await contextService.PrepareAsync(
            Guid.NewGuid(),
            "conceito desconhecido para a base",
            InteractionMode.Chat,
            CancellationToken.None);

        Assert.DoesNotContain("[knowledge]", prepared.ModelPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_context_is_not_augmented_by_chat_rag()
    {
        var store = new InMemoryKnowledgeStore();
        var item = CreateItem(
            title: "dotnet build",
            finalScore: 0.90,
            isDangerous: false,
            riskLevel: KnowledgeRiskLevel.Safe);
        item.Content = "dotnet build";
        await store.SaveAsync(
            item,
            [],
            [],
            new KnowledgeExperiment { KnowledgeItemId = item.Id });

        var contextService = CreateContextService(store);

        var prepared = await contextService.PrepareAsync(
            Guid.NewGuid(),
            "dotnet build",
            InteractionMode.Agent,
            CancellationToken.None);

        Assert.DoesNotContain("[knowledge]", prepared.ModelPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTaskLearning_persists_procedure_summary_from_run()
    {
        var store = new InMemoryKnowledgeStore();
        var llama = CreateLlamaThatThrows();
        var logger = new Mock<ILogger>();
        var service = new PostTaskLearningService(
            llama,
            store,
            new KnowledgeScoreEngine(),
            new JsonExtractor(),
            logger.Object);

        var learned = await service.TryLearnFromRunAsync(
            new PostTaskRunSnapshot(
                "Criar um script de hello world",
                ["New-Item -ItemType File -Path hello.py", "python hello.py"],
                ["hello.py"]),
            CancellationToken.None);

        Assert.True(learned);
        var details = await store.FindDetailsAsync(
            "hello", minimumScore: 0, cancellationToken: CancellationToken.None);
        var item = Assert.Single(details).Item;
        Assert.Equal(KnowledgeItemKind.Procedure, item.Kind);
        Assert.True(item.FinalScore >= 0.75);
        Assert.Contains("hello.py", item.Content, StringComparison.Ordinal);
        Assert.Equal("PostTaskLearningService", item.SourceName);
        Assert.False(item.IsExecutableAdvice);
    }

    [Fact]
    public async Task PostTaskLearning_returns_false_when_no_evidence_exists()
    {
        var store = new InMemoryKnowledgeStore();
        var service = new PostTaskLearningService(
            CreateLlamaThatThrows(),
            store,
            new KnowledgeScoreEngine(),
            new JsonExtractor(),
            new Mock<ILogger>().Object);

        var learned = await service.TryLearnFromRunAsync(
            new PostTaskRunSnapshot("sem acoes", [], []),
            CancellationToken.None);

        Assert.False(learned);
        var details = await store.FindDetailsAsync(
            "", minimumScore: 0, cancellationToken: CancellationToken.None);
        Assert.Empty(details);
    }

    private static ConversationContextService CreateContextService(IKnowledgeStore store)
    {
        var builder = new NebulaContextBuilder();
        var logger = new Mock<ILogger>();
        return new ConversationContextService(
            conversationRepository: null,
            builder,
            logger.Object,
            knowledgeQueryService: new KnowledgeQueryService(
                store,
                logger.Object));
    }

    private static KnowledgeItem CreateItem(
        string title,
        double finalScore,
        bool isDangerous,
        KnowledgeRiskLevel riskLevel)
    {
        var item = new KnowledgeItem
        {
            Domain = KnowledgeDomain.General,
            Kind = KnowledgeItemKind.Concept,
            Topic = title,
            Title = title,
            Content = title,
            Summary = title,
            Hash = $"{Guid.NewGuid():N}",
            IsDangerousInstruction = isDangerous,
            RiskLevel = riskLevel,
            FinalScore = finalScore
        };
        return item;
    }

    private static ILlamaClient CreateLlamaThatThrows()
    {
        var mock = new Mock<ILlamaClient>();
        mock.Setup(client => client.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        mock.Setup(client => client.GetResponseAsync(It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        return mock.Object;
    }
}