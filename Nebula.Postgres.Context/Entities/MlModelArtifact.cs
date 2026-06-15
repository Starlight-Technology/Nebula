namespace Nebula.Postgres.Context.Entities;

public sealed class MlModelArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public int Version { get; set; }

    public byte[] ModelData { get; set; } = [];

    public string? SchemaJson { get; set; }

    public double? Accuracy { get; set; }

    public double? F1Score { get; set; }

    public string? TrainingDatasetHash { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ActivatedAt { get; set; }
}
