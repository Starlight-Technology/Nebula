using Nebula.Core.Learning;
using Nebula.Services.Learning;

namespace Nebula.Agent.Test.Learning;

public sealed class KnowledgeSearchServiceTest
{
    [Fact]
    public async Task search_knowledge_must_rank_by_token_overlap()
    {
        var store = new InMemoryKnowledgeStore();
        await StoreItemAsync(
            store,
            title: "Compilando projetos dotnet",
            content: "Use dotnet build e dotnet test no projeto.",
            tags: "dotnet build");
        await StoreItemAsync(
            store,
            title: "Receitas de padaria",
            content: "Receitas de bolo, pao e torta.",
            tags: "cozinha");

        var service = new KnowledgeSearchService(store);
        var hits = await service.SearchKnowledgeAsync("dotnet build", maxResults: 2);

        Assert.NotEmpty(hits);
        Assert.True(hits[0].Item.Title.Contains("dotnet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task search_knowledge_must_return_empty_for_unmatched_query()
    {
        var store = new InMemoryKnowledgeStore();
        await StoreItemAsync(store, "dotnet", "dotnet build");

        var service = new KnowledgeSearchService(store);
        var hits = await service.SearchKnowledgeAsync("agricultura orgânica", maxResults: 2);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task search_project_must_find_workspace_files()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(
            Path.Combine(workspace.Path, "src.cs"),
            "public static void Main() { Console.WriteLine(\"dotnet beleza\"); }");
        File.WriteAllText(
            Path.Combine(workspace.Path, "notes.txt"),
            "lista de compras do mercado");

        var service = new KnowledgeSearchService(new InMemoryKnowledgeStore());
        var hits = await service.SearchProjectAsync(workspace.Path, "dotnet", maxResults: 5);

        var match = Assert.Single(
            hits,
            hit => hit.Path.EndsWith("src.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("dotnet", match.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task search_project_must_skip_generated_directories()
    {
        using var workspace = new TempTestWorkspace();
        var generatedDir = Path.Combine(workspace.Path, "bin");
        Directory.CreateDirectory(generatedDir);
        File.WriteAllText(
            Path.Combine(generatedDir, "artifact.cs"),
            "dotnet runtime artifact");

        var service = new KnowledgeSearchService(new InMemoryKnowledgeStore());
        var hits = await service.SearchProjectAsync(workspace.Path, "dotnet", maxResults: 5);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task search_project_must_return_empty_for_missing_workspace()
    {
        var service = new KnowledgeSearchService(new InMemoryKnowledgeStore());
        var hits = await service.SearchProjectAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            "dotnet");

        Assert.Empty(hits);
    }

    private static async Task StoreItemAsync(
        InMemoryKnowledgeStore store,
        string title,
        string content,
        string tags = "")
    {
        var item = new KnowledgeItem
        {
            Id = Guid.NewGuid(),
            Domain = KnowledgeDomain.DotNet,
            Kind = KnowledgeItemKind.Concept,
            Title = title,
            Topic = title,
            Content = content,
            Summary = content,
            Tags = tags,
            FinalScore = 0.9,
            SourceType = LearningSourceType.LocalFile,
            SourceName = "test.md"
        };
        await store.SaveAsync(
            item,
            [new KnowledgeSource { KnowledgeItemId = item.Id, Url = "file://test.md" }],
            facts: [],
            new KnowledgeExperiment { KnowledgeItemId = item.Id });
    }
}