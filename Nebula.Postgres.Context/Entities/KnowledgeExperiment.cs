using Nebula.Core.Learning;

namespace Nebula.Postgres.Context.Entities;

public sealed class KnowledgeExperiment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid KnowledgeItemId { get; set; }

    public VerificationKind VerificationKind { get; set; }

    public string? CommandExecuted { get; set; }

    public string? TestCode { get; set; }

    public int? ExitCode { get; set; }

    public string? StdOut { get; set; }

    public string? StdErr { get; set; }

    public bool Success { get; set; }

    public string EvidenceHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public KnowledgeItem? KnowledgeItem { get; set; }
}
