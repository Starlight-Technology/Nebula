using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Projects;

namespace Nebula.Agent;

public sealed class AgentActionRunRequest
{
    public const int DefaultMaxSteps = int.MaxValue;

    public const int DefaultMaxRetriesPerStep = int.MaxValue;

    public Guid ConversationId { get; set; }

    public Guid RequestId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public InteractionMode Mode { get; set; } = InteractionMode.Agent;

    public string ChatHistoryContext { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public int? MaxSteps { get; set; }

    public int? MaxRetriesPerStep { get; set; }

    /// <summary>
    /// Optional reference workspace root the agent should work on.
    /// When null, the runner resolves the default reference workspace.
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    public AgentApprovedAction? ApprovedAction { get; set; }

    /// <summary>
    /// Normalized commands already approved for this conversation. The runner
    /// skips approval for identical commands while processing this request.
    /// </summary>
    public IReadOnlyCollection<string>? ConversationApprovedCommands { get; set; }

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

    public IReadOnlyList<PlannedPatchFile>? PlannedFiles { get; set; }

    public string? WorkingDirectory { get; set; }

    public ApprovalScope Scope { get; set; } = ApprovalScope.Once;
}
