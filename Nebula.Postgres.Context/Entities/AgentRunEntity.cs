namespace Nebula.Postgres.Context.Entities;

public class AgentRunEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    public Guid RequestId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    public string? Response { get; set; }

    public bool IsCancelled { get; set; }

    public string? CurrentPlan { get; set; }

    public string? WorkspaceRoot { get; set; }

    public ICollection<AgentStepRecordEntity> Steps { get; set; } = [];

    public ICollection<AgentArtifactEntity> Artifacts { get; set; } = [];

    public ICollection<AgentApprovalEntity> Approvals { get; set; } = [];
}

public class AgentStepRecordEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }

    public int Step { get; set; } = 1;

    public int Attempt { get; set; } = 1;

    public string OperationKind { get; set; } = string.Empty;

    public string Objective { get; set; } = string.Empty;

    public string? Command { get; set; }

    public string? WorkingDirectory { get; set; }

    public string? TargetPath { get; set; }

    public int? ExitCode { get; set; }

    public bool Success { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? StandardOutput { get; set; }

    public string? StandardError { get; set; }

    public string? Shell { get; set; }

    public string? SafetyDecision { get; set; }

    public bool ApprovedByUser { get; set; }

    public bool AutoApproved { get; set; }

    public AgentRunEntity? Run { get; set; }
}

public class AgentArtifactEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Path { get; set; }

    public string? ContentHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AgentRunEntity? Run { get; set; }
}

public class AgentApprovalEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunId { get; set; }

    public Guid StepId { get; set; }

    public string Objective { get; set; } = string.Empty;

    public string? Command { get; set; }

    public string? Decision { get; set; }

    public bool ApprovedByUser { get; set; }

    public bool AutoApproved { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AgentRunEntity? Run { get; set; }
}
