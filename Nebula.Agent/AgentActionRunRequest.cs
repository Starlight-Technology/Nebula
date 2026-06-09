namespace Nebula.Agent;

public sealed class AgentActionRunRequest
{
    public const int DefaultMaxSteps = 20;

    public const int DefaultMaxRetriesPerStep = 5;

    public Guid ConversationId { get; set; }

    public Guid RequestId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string ChatHistoryContext { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public int? MaxSteps { get; set; }

    public int? MaxRetriesPerStep { get; set; }

    [Obsolete("Use MaxRetriesPerStep instead.")]
    public int? MaxRetries { get; set; }
}
