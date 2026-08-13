using Nebula.Core.Operations;
using Nebula.Core.Safety;

namespace Nebula.Agent.Application;

public sealed class CommandApprovalService : ICommandApprovalService
{
    public bool IsApprovalOverridable(OperationKind operationKind) =>
        operationKind is
            OperationKind.TerminalCommand or
            OperationKind.ScriptExecution or
            OperationKind.FileRead;

    public ApprovalOverrideResult EvaluateOverride(
        CommandSafetyDecision decision,
        OperationKind operationKind,
        ApprovalOverrideInput input)
    {
        if (decision.Decision != CommandSafetyDecisionType.AskApproval ||
            !IsApprovalOverridable(operationKind))
        {
            return new ApprovalOverrideResult(
                ApprovalOverrideSource.None,
                string.Empty);
        }

        if (input.HasUserApprovedAction)
        {
            return new ApprovalOverrideResult(
                ApprovalOverrideSource.Manual,
                "Aprovado manualmente pela interface.");
        }

        if (input.IsApprovedForConversation)
        {
            return new ApprovalOverrideResult(
                ApprovalOverrideSource.Conversation,
                "Aprovado para esta conversa: o comando ja havia sido aprovado neste dialogo.");
        }

        var category = CategorizeIntent(decision.Intent);
        if (CategoryMatched(category, input.AutoApproveCategories) ||
            CategoryMatched(category, input.WorkspaceAutoApproveCategories))
        {
            return new ApprovalOverrideResult(
                ApprovalOverrideSource.Category,
                $"Auto-aprovado pela categoria '{category}'.");
        }

        if (input.AutoApproveEnabled)
        {
            return new ApprovalOverrideResult(
                ApprovalOverrideSource.Auto,
                "Aprovado automaticamente pelas preferencias do runtime.");
        }

        return new ApprovalOverrideResult(
            ApprovalOverrideSource.None,
            string.Empty);
    }

    private static bool CategoryMatched(
        string category,
        IReadOnlyCollection<string> categories) =>
        categories.Count > 0 &&
        categories.Contains(category, StringComparer.OrdinalIgnoreCase);

    public static string CategorizeIntent(CommandIntent intent) =>
        intent switch
        {
            CommandIntent.PackageInstall => "package-install",
            CommandIntent.NetworkAccess => "network-access",
            CommandIntent.PrivilegedOperation => "privileged-operation",
            CommandIntent.DestructiveOperation => "destructive-operation",
            CommandIntent.DataExfiltration => "data-exfiltration",
            CommandIntent.SafeReadOnly => "read-only",
            CommandIntent.SafeWriteLocal => "write-local",
            CommandIntent.SafeExecuteLocal => "execute-local",
            CommandIntent.Blocked => "blocked",
            _ => "needs-approval"
        };
}
