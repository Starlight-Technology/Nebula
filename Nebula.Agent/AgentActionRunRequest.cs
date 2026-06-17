using Nebula.Core.Interactions;
using Nebula.Core.Operations;

namespace Nebula.Agent;

public sealed class AgentActionRunRequest
{
    public const int DefaultMaxSteps = 20;

    public const int DefaultMaxRetriesPerStep = 5;

    public Guid ConversationId { get; set; }

    public Guid RequestId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public InteractionMode Mode { get; set; } = InteractionMode.Agent;

    public string ChatHistoryContext { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public int? MaxSteps { get; set; }

    public int? MaxRetriesPerStep { get; set; }

    public AgentApprovedAction? ApprovedAction { get; set; }

    [Obsolete("Use MaxRetriesPerStep instead.")]
    public int? MaxRetries { get; set; }
}

public sealed class AgentApprovedAction
{
    public string Objective { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public OperationKind OperationKind { get; set; } =
        OperationKind.TerminalCommand;

    public string? TargetPath { get; set; }

    public string? WorkingDirectory { get; set; }
}
