using Nebula.Core.Execution;

namespace Nebula.Core.Configuration;

public sealed record SandboxSettings(
    string UserId,
    string Workspace,
    SandboxMode Mode,
    string Image,
    long MemoryLimitMb,
    double CpuLimit,
    DateTimeOffset UpdatedAt);

public interface ISandboxSettingsStore
{
    Task<SandboxSettings?> GetEffectiveAsync(
        string userId,
        string workspace,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        SandboxSettings settings,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string userId,
        string workspace,
        CancellationToken cancellationToken = default);
}
