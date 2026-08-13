using Nebula.Agent.Application;
using Nebula.Core.Operations;
using Nebula.Core.Safety;

namespace Nebula.Agent.Test;

public sealed class CommandApprovalServiceTest
{
    private static readonly CommandSafetyDecision AskApproval =
        new(CommandSafetyDecisionType.AskApproval, CommandIntent.NeedsApproval, 0.9, []);

    private static readonly CommandSafetyDecision Allowed =
        new(CommandSafetyDecisionType.Allow, CommandIntent.SafeReadOnly, 0.9, []);

    private static readonly CommandSafetyDecision Blocked =
        new(CommandSafetyDecisionType.Block, CommandIntent.DestructiveOperation, 0.9, []);

    private readonly CommandApprovalService service = new();

    private static ApprovalOverrideInput CreateInput(
        bool hasUserApprovedAction = false,
        bool isApprovedForConversation = false,
        bool autoApproveEnabled = false,
        IReadOnlyCollection<string>? autoApproveCategories = null,
        IReadOnlyCollection<string>? workspaceAutoApproveCategories = null) =>
        new(
            hasUserApprovedAction,
            isApprovedForConversation,
            autoApproveEnabled,
            autoApproveCategories ?? [],
            workspaceAutoApproveCategories ?? []);

    [Fact]
    public void is_approval_overridable_must_cover_terminal_script_and_read()
    {
        Assert.True(service.IsApprovalOverridable(OperationKind.TerminalCommand));
        Assert.True(service.IsApprovalOverridable(OperationKind.ScriptExecution));
        Assert.True(service.IsApprovalOverridable(OperationKind.FileRead));
        Assert.False(service.IsApprovalOverridable(OperationKind.FileWrite));
        Assert.False(service.IsApprovalOverridable(OperationKind.PlannedPatch));
        Assert.False(service.IsApprovalOverridable(OperationKind.Research));
    }

    [Fact]
    public void evaluate_override_must_require_ask_approval_decision()
    {
        var result = service.EvaluateOverride(
            Allowed,
            OperationKind.TerminalCommand,
            CreateInput(hasUserApprovedAction: true));

        Assert.Equal(ApprovalOverrideSource.None, result.Source);
        Assert.False(result.CanProceed);
    }

    [Fact]
    public void evaluate_override_must_reject_non_overridable_operations()
    {
        var result = service.EvaluateOverride(
            AskApproval,
            OperationKind.FileWrite,
            CreateInput(hasUserApprovedAction: true));

        Assert.Equal(ApprovalOverrideSource.None, result.Source);
        Assert.False(result.CanProceed);
    }

    [Fact]
    public void evaluate_override_must_require_some_approval_path()
    {
        var result = service.EvaluateOverride(
            AskApproval,
            OperationKind.TerminalCommand,
            CreateInput());

        Assert.Equal(ApprovalOverrideSource.None, result.Source);
        Assert.False(result.CanProceed);
    }

    [Fact]
    public void evaluate_override_must_prefer_manual_over_conversation_and_auto()
    {
        var result = service.EvaluateOverride(
            AskApproval,
            OperationKind.TerminalCommand,
            CreateInput(
                hasUserApprovedAction: true,
                isApprovedForConversation: true,
                autoApproveEnabled: true));

        Assert.Equal(ApprovalOverrideSource.Manual, result.Source);
        Assert.True(result.CanProceed);
        Assert.Contains("manual", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void evaluate_override_must_apply_conversation_approval()
    {
        var result = service.EvaluateOverride(
            AskApproval,
            OperationKind.TerminalCommand,
            CreateInput(isApprovedForConversation: true));

        Assert.Equal(ApprovalOverrideSource.Conversation, result.Source);
        Assert.True(result.CanProceed);
        Assert.Contains("conversa", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void evaluate_override_must_apply_category_auto_approval()
    {
        var result = service.EvaluateOverride(
            new CommandSafetyDecision(
                CommandSafetyDecisionType.AskApproval,
                CommandIntent.PackageInstall,
                0.9,
                []),
            OperationKind.TerminalCommand,
            CreateInput(autoApproveCategories: ["package-install"]));

        Assert.Equal(ApprovalOverrideSource.Category, result.Source);
        Assert.True(result.CanProceed);
        Assert.Contains("package-install", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void evaluate_override_must_apply_workspace_category_auto_approval()
    {
        var result = service.EvaluateOverride(
            new CommandSafetyDecision(
                CommandSafetyDecisionType.AskApproval,
                CommandIntent.NetworkAccess,
                0.9,
                []),
            OperationKind.TerminalCommand,
            CreateInput(workspaceAutoApproveCategories: ["network-access"]));

        Assert.Equal(ApprovalOverrideSource.Category, result.Source);
        Assert.True(result.CanProceed);
        Assert.Contains("network-access", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void evaluate_override_must_combine_global_and_workspace_categories()
    {
        var result = service.EvaluateOverride(
            new CommandSafetyDecision(
                CommandSafetyDecisionType.AskApproval,
                CommandIntent.PackageInstall,
                0.9,
                []),
            OperationKind.TerminalCommand,
            CreateInput(
                autoApproveCategories: ["read-only"],
                workspaceAutoApproveCategories: ["package-install"]));

        Assert.Equal(ApprovalOverrideSource.Category, result.Source);
        Assert.True(result.CanProceed);
        Assert.Contains("package-install", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void evaluate_override_must_prefer_manual_over_workspace_categories()
    {
        var result = service.EvaluateOverride(
            AskApproval,
            OperationKind.TerminalCommand,
            CreateInput(
                hasUserApprovedAction: true,
                workspaceAutoApproveCategories: ["needs-approval"]));

        Assert.Equal(ApprovalOverrideSource.Manual, result.Source);
        Assert.True(result.CanProceed);
        Assert.Contains("manual", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void evaluate_override_must_apply_global_auto_approval_as_last_resort()
    {
        var result = service.EvaluateOverride(
            AskApproval,
            OperationKind.ScriptExecution,
            CreateInput(autoApproveEnabled: true));

        Assert.Equal(ApprovalOverrideSource.Auto, result.Source);
        Assert.True(result.CanProceed);
        Assert.Contains("automatic", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void evaluate_override_must_apply_to_blocked_decisions_as_none()
    {
        var result = service.EvaluateOverride(
            Blocked,
            OperationKind.TerminalCommand,
            CreateInput(hasUserApprovedAction: true, autoApproveEnabled: true));

        Assert.Equal(ApprovalOverrideSource.None, result.Source);
        Assert.False(result.CanProceed);
    }

    [Theory]
    [InlineData(CommandIntent.PackageInstall, "package-install")]
    [InlineData(CommandIntent.NetworkAccess, "network-access")]
    [InlineData(CommandIntent.PrivilegedOperation, "privileged-operation")]
    [InlineData(CommandIntent.DestructiveOperation, "destructive-operation")]
    [InlineData(CommandIntent.DataExfiltration, "data-exfiltration")]
    [InlineData(CommandIntent.SafeReadOnly, "read-only")]
    [InlineData(CommandIntent.SafeWriteLocal, "write-local")]
    [InlineData(CommandIntent.SafeExecuteLocal, "execute-local")]
    [InlineData(CommandIntent.Unknown, "needs-approval")]
    public void categorize_intent_must_map_known_intents(CommandIntent intent, string expected)
    {
        Assert.Equal(expected, CommandApprovalService.CategorizeIntent(intent));
    }
}
