using Microsoft.EntityFrameworkCore;

using Nebula.Core.Configuration;
using Nebula.Core.Execution;

using SandboxSettingsEntity = Nebula.Postgres.Context.Entities.SandboxSettingsEntity;

namespace Nebula.Postgres.Context;

public sealed class PostgresSandboxSettingsStore(PostgresContext context)
    : ISandboxSettingsStore
{
    public async Task<SandboxSettings?> GetEffectiveAsync(
        string userId,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        var normalizedWorkspace = workspace?.Trim() ?? string.Empty;
        var entity = await context.SandboxSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value =>
                    value.UserId == userId &&
                    value.Workspace == normalizedWorkspace,
                cancellationToken);

        if (entity is null && normalizedWorkspace.Length > 0)
        {
            entity = await context.SandboxSettings
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    value =>
                        value.UserId == userId &&
                        value.Workspace == string.Empty,
                    cancellationToken);
        }

        return entity is null ? null : Map(entity);
    }

    public async Task SaveAsync(
        SandboxSettings settings,
        CancellationToken cancellationToken = default)
    {
        var workspace = settings.Workspace?.Trim() ?? string.Empty;
        var entity = await context.SandboxSettings
            .SingleOrDefaultAsync(
                value =>
                    value.UserId == settings.UserId &&
                    value.Workspace == workspace,
                cancellationToken);
        var updatedAt = settings.UpdatedAt == default
            ? DateTimeOffset.UtcNow
            : settings.UpdatedAt;

        if (entity is null)
        {
            entity = new SandboxSettingsEntity
            {
                UserId = settings.UserId,
                Workspace = workspace,
                Mode = (int)NormalizeMode(settings.Mode),
                Image = NormalizeImage(settings.Image),
                MemoryLimitMb = Math.Max(0, settings.MemoryLimitMb),
                CpuLimit = Math.Max(0, settings.CpuLimit),
                UpdatedAt = updatedAt
            };
            context.SandboxSettings.Add(entity);
        }
        else
        {
            entity.Mode = (int)NormalizeMode(settings.Mode);
            entity.Image = NormalizeImage(settings.Image);
            entity.MemoryLimitMb = Math.Max(0, settings.MemoryLimitMb);
            entity.CpuLimit = Math.Max(0, settings.CpuLimit);
            entity.UpdatedAt = updatedAt;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        string userId,
        string workspace,
        CancellationToken cancellationToken = default)
    {
        var normalizedWorkspace = workspace?.Trim() ?? string.Empty;
        var entity = await context.SandboxSettings
            .SingleOrDefaultAsync(
                value =>
                    value.UserId == userId &&
                    value.Workspace == normalizedWorkspace,
                cancellationToken);

        if (entity is null)
        {
            return;
        }

        context.SandboxSettings.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static SandboxSettings Map(SandboxSettingsEntity entity) =>
        new(
            entity.UserId,
            entity.Workspace,
            NormalizeMode((SandboxMode)entity.Mode),
            NormalizeImage(entity.Image),
            Math.Max(0, entity.MemoryLimitMb),
            Math.Max(0, entity.CpuLimit),
            entity.UpdatedAt);

    private static SandboxMode NormalizeMode(SandboxMode mode) =>
        Enum.IsDefined(mode) ? mode : SandboxMode.Disabled;

    private static string NormalizeImage(string? image) =>
        NebulaRuntimeSettings.NormalizeSandboxImage(image);
}
