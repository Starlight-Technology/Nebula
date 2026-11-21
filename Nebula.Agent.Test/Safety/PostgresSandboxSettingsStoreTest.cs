using Microsoft.EntityFrameworkCore;

using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Postgres.Context;

namespace Nebula.Agent.Test.Safety;

public sealed class PostgresSandboxSettingsStoreTest
{
    [Fact]
    public async Task save_then_get_must_round_trip_settings()
    {
        var context = CreateContext();
        var store = new PostgresSandboxSettingsStore(context);
        var settings = new SandboxSettings(
            "user-a",
            "/workspace/a",
            SandboxMode.Docker,
            "mcr.microsoft.com/powershell:lts",
            1024,
            1.5,
            DateTimeOffset.UtcNow);

        await store.SaveAsync(settings);

        var loaded = await store.GetEffectiveAsync("user-a", "/workspace/a");
        Assert.NotNull(loaded);
        Assert.Equal(SandboxMode.Docker, loaded!.Mode);
        Assert.Equal("mcr.microsoft.com/powershell:lts", loaded.Image);
        Assert.Equal(1024, loaded.MemoryLimitMb);
        Assert.Equal(1.5, loaded.CpuLimit);
    }

    [Fact]
    public async Task workspace_must_fall_back_to_global_settings()
    {
        var context = CreateContext();
        var store = new PostgresSandboxSettingsStore(context);
        await store.SaveAsync(new SandboxSettings(
            "user-a",
            string.Empty,
            SandboxMode.Docker,
            "global-image",
            512,
            1,
            DateTimeOffset.UtcNow));

        var loaded = await store.GetEffectiveAsync("user-a", "/workspace/a");

        Assert.NotNull(loaded);
        Assert.Equal(string.Empty, loaded!.Workspace);
        Assert.Equal("global-image", loaded.Image);
    }

    [Fact]
    public async Task workspace_settings_must_override_global_settings()
    {
        var context = CreateContext();
        var store = new PostgresSandboxSettingsStore(context);
        await store.SaveAsync(new SandboxSettings(
            "user-a",
            string.Empty,
            SandboxMode.Docker,
            "global-image",
            512,
            1,
            DateTimeOffset.UtcNow));
        await store.SaveAsync(new SandboxSettings(
            "user-a",
            "/workspace/a",
            SandboxMode.Disabled,
            "workspace-image",
            2048,
            2,
            DateTimeOffset.UtcNow));

        var loaded = await store.GetEffectiveAsync("user-a", "/workspace/a");

        Assert.NotNull(loaded);
        Assert.Equal("/workspace/a", loaded!.Workspace);
        Assert.Equal(SandboxMode.Disabled, loaded.Mode);
        Assert.Equal("workspace-image", loaded.Image);
    }

    [Fact]
    public async Task save_duplicate_scope_must_upsert_not_duplicate()
    {
        var context = CreateContext();
        var store = new PostgresSandboxSettingsStore(context);
        await store.SaveAsync(new SandboxSettings(
            "user-a",
            "/workspace/a",
            SandboxMode.Disabled,
            "first-image",
            512,
            1,
            DateTimeOffset.UtcNow));
        await store.SaveAsync(new SandboxSettings(
            "user-a",
            "/workspace/a",
            SandboxMode.Docker,
            "second-image",
            1024,
            2,
            DateTimeOffset.UtcNow));

        var loaded = await store.GetEffectiveAsync("user-a", "/workspace/a");
        Assert.NotNull(loaded);
        Assert.Equal("second-image", loaded!.Image);
        Assert.Equal(SandboxMode.Docker, loaded.Mode);
        Assert.Single(await context.SandboxSettings.ToListAsync());
    }

    [Fact]
    public async Task save_invalid_image_must_normalize_to_default()
    {
        var context = CreateContext();
        var store = new PostgresSandboxSettingsStore(context);
        await store.SaveAsync(new SandboxSettings(
            "user-a",
            "/workspace/a",
            SandboxMode.Docker,
            "--privileged alpine",
            0,
            0,
            DateTimeOffset.UtcNow));

        var loaded = await store.GetEffectiveAsync("user-a", "/workspace/a");

        Assert.NotNull(loaded);
        Assert.Equal(NebulaRuntimeSettings.DefaultSandboxImage, loaded!.Image);
    }

    [Fact]
    public async Task delete_must_remove_workspace_override_and_restore_global_fallback()
    {
        var context = CreateContext();
        var store = new PostgresSandboxSettingsStore(context);
        await store.SaveAsync(new SandboxSettings(
            "user-a",
            string.Empty,
            SandboxMode.Docker,
            "global-image",
            512,
            1,
            DateTimeOffset.UtcNow));
        await store.SaveAsync(new SandboxSettings(
            "user-a",
            "/workspace/a",
            SandboxMode.Disabled,
            "workspace-image",
            2048,
            2,
            DateTimeOffset.UtcNow));

        await store.DeleteAsync("user-a", "/workspace/a");

        var loaded = await store.GetEffectiveAsync("user-a", "/workspace/a");
        Assert.NotNull(loaded);
        Assert.Equal(string.Empty, loaded!.Workspace);
        Assert.Equal("global-image", loaded.Image);
        Assert.Equal(SandboxMode.Docker, loaded.Mode);
    }

    [Fact]
    public async Task users_must_be_isolated()
    {
        var context = CreateContext();
        var store = new PostgresSandboxSettingsStore(context);
        await store.SaveAsync(new SandboxSettings(
            "user-a",
            string.Empty,
            SandboxMode.Docker,
            "image-a",
            512,
            1,
            DateTimeOffset.UtcNow));

        var loaded = await store.GetEffectiveAsync("user-b", "/workspace/a");

        Assert.Null(loaded);
    }

    [Fact]
    public async Task missing_settings_must_return_null()
    {
        var context = CreateContext();
        var store = new PostgresSandboxSettingsStore(context);

        var loaded = await store.GetEffectiveAsync("user-a", "/workspace/a");

        Assert.Null(loaded);
    }

    private static PostgresContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PostgresContext>()
            .UseInMemoryDatabase($"nebula-sandbox-{Guid.NewGuid():N}")
            .Options;
        return new PostgresContext(options);
    }
}
