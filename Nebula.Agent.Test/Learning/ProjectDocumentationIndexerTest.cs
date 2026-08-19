using Nebula.Core.Learning;
using Nebula.Services.Learning;

namespace Nebula.Agent.Test.Learning;

public sealed class ProjectDocumentationIndexerTest
{
    [Fact]
    public async Task index_must_create_items_from_readme_and_docs()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(
            Path.Combine(workspace.Path, "README.md"),
            "# Meu Projeto\n\nProjeto de automacao local.\n\n## Build\n\nUse `dotnet build` para compilar.\n\n```csharp\nConsole.WriteLine(\"oi\");\n```");

        var indexer = new ProjectDocumentationIndexer(new InMemoryKnowledgeStore());
        var result = await indexer.IndexAsync(workspace.Path);

        Assert.True(result.Success);
        Assert.True(result.CreatedCount > 0);
        Assert.Contains(
            "Indice de documentacao atualizado",
            result.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task index_must_be_idempotent_by_content_hash()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(
            Path.Combine(workspace.Path, "README.md"),
            "# Projeto\n\nUse `dotnet build` para compilar.");

        var store = new InMemoryKnowledgeStore();
        var indexer = new ProjectDocumentationIndexer(store);

        var first = await indexer.IndexAsync(workspace.Path);
        var second = await indexer.IndexAsync(workspace.Path);

        Assert.True(first.CreatedCount > 0);
        Assert.Equal(0, second.CreatedCount);
        Assert.True(second.SkippedCount > 0);
    }

    [Fact]
    public async Task index_must_store_command_items_with_normalized_command()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(
            Path.Combine(workspace.Path, "README.md"),
            "# Projeto\n\nRode `dotnet test` para validar.");

        var store = new InMemoryKnowledgeStore();
        var indexer = new ProjectDocumentationIndexer(store);
        await indexer.IndexAsync(workspace.Path);

        var details = await store.FindDetailsAsync("dotnet", minimumScore: 0);
        Assert.Contains(
            details,
            result =>
                result.Item.Kind == KnowledgeItemKind.Command &&
                result.Item.NormalizedCommand == "dotnet test");
    }

    [Fact]
    public async Task index_must_return_not_success_for_missing_workspace()
    {
        var indexer = new ProjectDocumentationIndexer(new InMemoryKnowledgeStore());
        var result = await indexer.IndexAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.False(result.Success);
        Assert.Equal(0, result.CreatedCount);
    }
}