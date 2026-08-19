using Moq;

using Nebula.Agent.Application;
using Nebula.Core.Memory;
using Nebula.Services.Memory;

namespace Nebula.Agent.Test;

public sealed class UserMemoryServiceTest
{
    [Fact]
    public async Task set_preference_must_store_entry()
    {
        var store = new InMemoryUserMemoryStore();
        var service = CreateService(store);

        await service.SetPreferenceAsync("default", UserMemoryKind.Language, "Language", "pt-BR");

        var entries = await store.GetRecentAsync("default");
        var entry = Assert.Single(entries, value => value.Kind == UserMemoryKind.Language);
        Assert.Equal("pt-BR", entry.Value);
    }

    [Fact]
    public async Task set_preference_must_upsert()
    {
        var store = new InMemoryUserMemoryStore();
        var service = CreateService(store);

        await service.SetPreferenceAsync("default", UserMemoryKind.Style, "Style", "formal");
        await service.SetPreferenceAsync("default", UserMemoryKind.Style, "Style", "direto");

        var entries = await store.GetRecentAsync("default");
        var style = Assert.Single(entries, value => value.Kind == UserMemoryKind.Style);
        Assert.Equal("direto", style.Value);
    }

    [Fact]
    public async Task set_preference_must_ignore_blank_value()
    {
        var store = new InMemoryUserMemoryStore();
        var service = CreateService(store);

        await service.SetPreferenceAsync("default", UserMemoryKind.DetailLevel, "DetailLevel", "   ");

        var entries = await store.GetRecentAsync("default");
        Assert.Empty(entries);
    }

    [Fact]
    public async Task summary_must_include_all_preferences()
    {
        var store = new InMemoryUserMemoryStore();
        var service = CreateService(store);
        await service.SetPreferenceAsync("default", UserMemoryKind.Language, "Language", "pt-BR");
        await service.SetPreferenceAsync("default", UserMemoryKind.DetailLevel, "DetailLevel", "detalhado");

        var summary = await service.BuildUserPreferencesSummaryAsync("default");

        Assert.Contains("Idioma: pt-BR", summary);
        Assert.Contains("Nivel de detalhe: detalhado", summary);
    }

    [Fact]
    public async Task summary_must_be_empty_for_unknown_user()
    {
        var store = new InMemoryUserMemoryStore();
        var service = CreateService(store);

        var summary = await service.BuildUserPreferencesSummaryAsync("someone-else");

        Assert.Equal(string.Empty, summary);
    }

    private static UserMemoryService CreateService(IUserMemoryStore store) =>
        new(store, new Mock<ILogger>().Object);
}