using System;

namespace Nebula.Postgres.Context.Entities;

public class ConversationState
{
    public Guid ConversationId { get; set; }

    public string? Summary { get; set; }

    public string? CurrentGoal { get; set; }

    public string? CurrentPlan { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
