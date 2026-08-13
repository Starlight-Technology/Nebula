using Microsoft.EntityFrameworkCore;

using Nebula.Core.Memory;
using Nebula.Postgres.Context;

namespace Nebula.Agent.Test.Safety;

public sealed class PostgresWorkspaceMemoryStoreTest
{
    [Fact]
    public async Task save_then_get_must_round_trip_entries()
    {
        var context = CreateContext();
        var store = new PostgresWorkspaceMemoryStore(context);
        var entry = new WorkspaceMemoryEntry(
            Guid.NewGuid(),
            "C:\\repos\\alpha",
            WorkspaceMemoryKind.WorkingCommand,
            "dotnet build",
            "dotnet build",
            "exitCode=0",
            DateTimeOffset.UtcNow);

        await store.SaveAsync(entry);

        var loaded = await store.GetRecentAsync("C:\\repos\\alpha");
        Assert.Single(loaded);
        Assert.Equal("dotnet build", loaded[0].Value);
        Assert.Equal(WorkspaceMemoryKind.WorkingCommand, loaded[0].Kind);
    }

    [Fact]
    public async Task save_duplicate_key_must_upsert_not_duplicate()
    {
        var context = CreateContext();
        var store = new PostgresWorkspaceMemoryStore(context);
        var first = new WorkspaceMemoryEntry(
            Guid.NewGuid(),
            "ws",
            WorkspaceMemoryKind.UsedPort,
            "8080",
            "8080",
            "old",
            DateTimeOffset.UtcNow);
        await store.SaveAsync(first);

        var updated = new WorkspaceMemoryEntry(
            Guid.NewGuid(),
            "ws",
            WorkspaceMemoryKind.UsedPort,
            "8080",
            "8080",
            "new",
            DateTimeOffset.UtcNow);
        await store.SaveAsync(updated);

        var loaded = await store.GetRecentAsync("ws");
        Assert.Single(loaded);
        Assert.Equal("new", loaded[0].Evidence);
    }

    [Fact]
    public async Task workspace_must_be_isolated()
    {
        var context = CreateContext();
        var store = new PostgresWorkspaceMemoryStore(context);
        await store.SaveAsync(new WorkspaceMemoryEntry(
            Guid.NewGuid(),
            "ws-a",
            WorkspaceMemoryKind.WorkingCommand,
            "npm test",
            "npm test",
            null,
            DateTimeOffset.UtcNow));

        var others = await store.GetRecentAsync("ws-b");
        Assert.Empty(others);
    }

    [Fact]
    public async Task exists_must_detect_saved_entries()
    {
        var context = CreateContext();
        var store = new PostgresWorkspaceMemoryStore(context);
        await store.SaveAsync(new WorkspaceMemoryEntry(
            Guid.NewGuid(),
            "ws",
            WorkspaceMemoryKind.Script,
            "run.ps1",
            "run.ps1",
            null,
            DateTimeOffset.UtcNow));

        Assert.True(await store.ExistsAsync("ws", WorkspaceMemoryKind.Script, "run.ps1"));
        Assert.False(await store.ExistsAsync("ws", WorkspaceMemoryKind.Script, "other.ps1"));
    }

    private static PostgresContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PostgresContext>()
            .UseInMemoryDatabase($"nebula-memory-{Guid.NewGuid():N}")
            .Options;
        return new PostgresContext(options);
    }
}