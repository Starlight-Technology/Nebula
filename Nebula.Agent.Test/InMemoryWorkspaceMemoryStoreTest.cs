using Nebula.Core.Memory;
using Nebula.Services.Memory;

namespace Nebula.Agent.Test;

public sealed class InMemoryWorkspaceMemoryStoreTest
{
    [Fact]
    public async Task save_async_must_upsert_by_workspace_kind_key()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var first = new WorkspaceMemoryEntry(
            Guid.NewGuid(),
            "ws",
            WorkspaceMemoryKind.Note,
            "key",
            "old",
            null,
            DateTimeOffset.UtcNow);
        var second = first with
        {
            Id = Guid.NewGuid(),
            Value = "new",
            Evidence = "evidence",
            CreatedAt = first.CreatedAt.AddMinutes(1)
        };

        await store.SaveAsync(first);
        await store.SaveAsync(second);

        var entries = await store.GetRecentAsync("ws");
        var matching = entries
            .Where(entry =>
                entry.Kind == WorkspaceMemoryKind.Note && entry.Key == "key")
            .ToList();
        Assert.Single(matching);
        Assert.Equal(first.Id, matching[0].Id);
        Assert.Equal("new", matching[0].Value);
        Assert.Equal("evidence", matching[0].Evidence);
    }

    [Fact]
    public async Task save_async_must_match_key_case_insensitively()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var first = new WorkspaceMemoryEntry(
            Guid.NewGuid(),
            "WS",
            WorkspaceMemoryKind.Note,
            "Key",
            "old",
            null,
            DateTimeOffset.UtcNow);

        await store.SaveAsync(first);
        await store.SaveAsync(first with { Value = "new" });

        var entries = await store.GetRecentAsync("ws");
        Assert.Single(entries);
        Assert.Equal("new", entries[0].Value);
    }

    [Fact]
    public async Task exists_async_must_find_upserted_entry()
    {
        var store = new InMemoryWorkspaceMemoryStore();
        var entry = new WorkspaceMemoryEntry(
            Guid.NewGuid(),
            "ws",
            WorkspaceMemoryKind.Strategy,
            "stack|error",
            "dotnet build",
            null,
            DateTimeOffset.UtcNow);

        await store.SaveAsync(entry);
        await store.SaveAsync(entry with { Value = "dotnet test" });

        Assert.True(await store.ExistsAsync("ws", WorkspaceMemoryKind.Strategy, "stack|error"));
        var entries = await store.GetRecentAsync("ws");
        Assert.Single(entries);
    }
}
