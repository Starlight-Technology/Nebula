using Nebula.Core.Operations;
using Nebula.Core.Safety;

namespace Nebula.Agent;

public enum ApprovalOverrideSource
{
    None,
    Manual,
    Conversation,
    Workspace,
    Category,
    Auto
}

public enum ApprovalScope
{
    Once,
    Conversation,
    Workspace,
    Category
}

public sealed record ApprovalOverrideResult(
    ApprovalOverrideSource Source,
    string Note)
{
    public bool CanProceed => Source != ApprovalOverrideSource.None;
}

public sealed record ApprovalOverrideInput(
    bool HasUserApprovedAction,
    bool IsApprovedForConversation,
    bool AutoApproveEnabled,
    IReadOnlyCollection<string> AutoApproveCategories,
    IReadOnlyCollection<string> WorkspaceAutoApproveCategories);

public interface ICommandApprovalService
{
    bool IsApprovalOverridable(OperationKind operationKind);

    ApprovalOverrideResult EvaluateOverride(
        CommandSafetyDecision decision,
        OperationKind operationKind,
        ApprovalOverrideInput input);
}
