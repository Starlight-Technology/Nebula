namespace Nebula.Agent.Data;

public class ConversationState
{
    public Guid ConversationId { get; set; }

    public string? Summary { get; set; }

    public string? CurrentGoal { get; set; }

    public string? CurrentPlan { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
