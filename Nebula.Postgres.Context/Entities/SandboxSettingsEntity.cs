namespace Nebula.Postgres.Context.Entities;

public class SandboxSettingsEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserId { get; set; } = string.Empty;

    public string Workspace { get; set; } = string.Empty;

    public int Mode { get; set; }

    public string Image { get; set; } = string.Empty;

    public long MemoryLimitMb { get; set; }

    public double CpuLimit { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
