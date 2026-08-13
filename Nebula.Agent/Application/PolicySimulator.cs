using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Memory;
using Nebula.Core.Operations;
using Nebula.Core.Safety;

namespace Nebula.Agent.Application;

public sealed class PolicySimulator(
    ICommandIntentParser intentParser,
    ICommandResolver resolver,
    IRuntimeCommandEnvironmentDetector environmentDetector,
    IOperationKindDetector operationKindDetector,
    ICommandPolicyEngine commandPolicyEngine,
    IOperationPolicyEngine operationPolicyEngine,
    ICommandApprovalService? approvalService = null,
    IWorkspaceCategoryPolicyService? workspaceCategoryPolicyService = null,
    ILogger? logger = null) : IPolicySimulator
{
    private readonly ICommandApprovalService approvalService =
        approvalService ?? new CommandApprovalService();

    public async Task<PolicySimulationResult> SimulateAsync(
        string text,
        string? rawCommand = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var workingDirectoryResolved = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;

        try
        {
            var step = new AgentStep
            {
                OriginalText = text,
                Command = rawCommand,
                WorkingDirectory = workingDirectoryResolved
            };
            var operationKind = operationKindDetector.Detect(step);
            if (operationKind != OperationKind.TerminalCommand)
            {
                return new PolicySimulationResult(
                    text,
                    operationKind,
                    CommandIntent.Unknown,
                    "n/a",
                    0,
                    "n/a",
                    [],
                    CommandSafetyDecisionType.AskApproval,
                    [
                        "O simulador de policy cobre comandos de terminal; " +
                        $"o texto foi detectado como '{operationKind}'."
                    ],
                    null,
                    null,
                    workingDirectoryResolved,
                    null,
                    null);
            }

            var environment = environmentDetector.Detect(workingDirectoryResolved);
            var commandRequest = intentParser.Parse(
                text,
                rawCommand,
                workingDirectoryResolved);
            var resolvedCommand = resolver.Resolve(commandRequest, environment);
            var commandText = resolvedCommand.DisplayCommand;

            var safetyDecision = await commandPolicyEngine.EvaluateAsync(
                commandText,
                cancellationToken);
            var operationDecision = await operationPolicyEngine.EvaluateAsync(
                new OperationPolicyRequest(
                    Guid.Empty,
                    Guid.Empty,
                    OperationKind.TerminalCommand,
                    text,
                    commandText,
                    null,
                    new CommandClassification(
                        commandText,
                        safetyDecision.Intent,
                        safetyDecision.Confidence,
                        "CommandSafetyClassifier",
                        safetyDecision.Reasons)),
                cancellationToken);

            var category = CommandApprovalService.CategorizeIntent(
                operationDecision.Intent);

            string? approvalOutcome = null;
            string? approvalNote = null;
            if (operationDecision.Decision == CommandSafetyDecisionType.AskApproval &&
                approvalService.IsApprovalOverridable(OperationKind.TerminalCommand))
            {
                IReadOnlyList<WorkspaceMemoryEntry> workspaceCategories = [];
                if (workspaceCategoryPolicyService is not null)
                {
                    workspaceCategories =
                        await workspaceCategoryPolicyService.ListAsync(
                            workingDirectoryResolved,
                            cancellationToken);
                }

                var overrideResult = approvalService.EvaluateOverride(
                    operationDecision,
                    OperationKind.TerminalCommand,
                    new ApprovalOverrideInput(
                        HasUserApprovedAction: false,
                        IsApprovedForConversation: false,
                        AutoApproveEnabled: false,
                        AutoApproveCategories: [],
                        WorkspaceAutoApproveCategories: workspaceCategories
                            .Select(entry => entry.Value)
                            .ToList()));
                approvalOutcome = overrideResult.CanProceed
                    ? overrideResult.Source.ToString()
                    : null;
                approvalNote = overrideResult.CanProceed
                    ? overrideResult.Note
                    : null;
            }

            return new PolicySimulationResult(
                text,
                operationKind,
                operationDecision.Intent,
                category,
                operationDecision.Confidence,
                "CommandSafetyClassifier",
                safetyDecision.Reasons,
                operationDecision.Decision,
                operationDecision.Reasons,
                commandText,
                environment.Shell.ToString(),
                workingDirectoryResolved,
                approvalOutcome,
                approvalNote);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Log(
                $"[POLICY-SIMULATOR] Simulation failed: {ex.Message}");
            return new PolicySimulationResult(
                text,
                OperationKind.Unknown,
                CommandIntent.Unknown,
                "needs-approval",
                0,
                "error",
                [ex.Message],
                CommandSafetyDecisionType.AskApproval,
                [ex.Message],
                null,
                null,
                workingDirectoryResolved,
                null,
                null);
        }
    }
}