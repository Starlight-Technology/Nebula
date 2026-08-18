using Moq;

using Nebula.Agent.Application;
using Nebula.Core.Commands;
using Nebula.Core.Memory;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Runner;
using Nebula.Services.Commands;
using Nebula.Services.Operations;

namespace Nebula.Agent.Test;

public sealed class PolicySimulatorTest
{
    private static ICommandPolicyEngine CreateCommandPolicyEngine(
        CommandSafetyDecision decision)
    {
        var mock = new Mock<ICommandPolicyEngine>();
        mock.Setup(engine => engine.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(decision);
        return mock.Object;
    }

    private static IOperationPolicyEngine CreateOperationPolicyEngine(
        CommandSafetyDecision decision)
    {
        var mock = new Mock<IOperationPolicyEngine>();
        mock.Setup(engine => engine.EvaluateAsync(
                It.IsAny<OperationPolicyRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(decision);
        return mock.Object;
    }

    private static PolicySimulator CreateSimulator(
        IOperationPolicyEngine? operationPolicyEngine = null,
        IWorkspaceCategoryPolicyService? workspaceCategoryPolicyService = null)
    {
        return new PolicySimulator(
            new CommandIntentParser(),
            new CommandResolver(),
            new RuntimeCommandEnvironmentDetector(),
            new OperationKindDetector(),
            CreateCommandPolicyEngine(new CommandSafetyDecision(
                CommandSafetyDecisionType.AskApproval,
                CommandIntent.PackageInstall,
                0.99,
                ["Package installation changes the local dependency set."])),
            operationPolicyEngine ?? CreateOperationPolicyEngine(new CommandSafetyDecision(
                CommandSafetyDecisionType.AskApproval,
                CommandIntent.PackageInstall,
                0.99,
                ["Policy requires explicit approval before execution."])),
            workspaceCategoryPolicyService: workspaceCategoryPolicyService);
    }

    [Fact]
    public async Task simulate_async_must_report_policy_decision_for_terminal_command()
    {
        var simulator = CreateSimulator();

        var result = await simulator.SimulateAsync("instale o pacote numpy");

        Assert.Equal(OperationKind.TerminalCommand, result.OperationKind);
        Assert.Equal(CommandIntent.PackageInstall, result.Intent);
        Assert.Equal("package-install", result.Category);
        Assert.Equal(CommandSafetyDecisionType.AskApproval, result.Decision);
        Assert.False(result.WouldRunWithoutApproval);
        Assert.Null(result.ApprovalOutcome);
        Assert.False(string.IsNullOrWhiteSpace(result.ResolvedCommand));
        Assert.False(string.IsNullOrWhiteSpace(result.Shell));
        Assert.NotEmpty(result.ClassificationReasons);
        Assert.NotEmpty(result.DecisionReasons);
    }

    [Fact]
    public async Task simulate_async_must_allow_execution_without_approval_when_policy_allows()
    {
        var simulator = CreateSimulator(
            operationPolicyEngine: CreateOperationPolicyEngine(new CommandSafetyDecision(
                CommandSafetyDecisionType.Allow,
                CommandIntent.SafeReadOnly,
                0.99,
                ["Allowed by deterministic classifier."])));

        var result = await simulator.SimulateAsync("listar arquivos");

        Assert.Equal(CommandSafetyDecisionType.Allow, result.Decision);
        Assert.True(result.WouldRunWithoutApproval);
        Assert.Null(result.ApprovalOutcome);
    }

    [Fact]
    public async Task simulate_async_must_report_workspace_category_outcome()
    {
        var categoryService = new Mock<IWorkspaceCategoryPolicyService>();
        categoryService
            .Setup(service => service.ListAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new WorkspaceMemoryEntry(
                    Guid.NewGuid(),
                    "C:\\ws",
                    WorkspaceMemoryKind.AutoApprovedCategory,
                    "package-install",
                    "package-install",
                    null,
                    DateTimeOffset.UtcNow)
            ]);

        var simulator = CreateSimulator(
            workspaceCategoryPolicyService: categoryService.Object);

        var result = await simulator.SimulateAsync(
            "instale o pacote numpy",
            workingDirectory: "C:\\ws");

        Assert.Equal(CommandSafetyDecisionType.AskApproval, result.Decision);
        Assert.True(result.WouldRunWithoutApproval);
        Assert.Equal("Category", result.ApprovalOutcome);
        Assert.Contains(
            "package-install",
            result.ApprovalNote ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task simulate_async_must_report_non_terminal_kinds_without_executing()
    {
        var simulator = CreateSimulator();

        var result = await simulator.SimulateAsync(
            "escreva um arquivo para mim");

        Assert.True(
            result.OperationKind is OperationKind.FileWrite or OperationKind.Unknown);
        Assert.False(result.WouldRunWithoutApproval);
    }

    [Fact]
    public async Task simulate_async_must_report_failure_without_throwing()
    {
        var badPolicy = new Mock<ICommandPolicyEngine>();
        badPolicy
            .Setup(engine => engine.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var simulator = new PolicySimulator(
            new CommandIntentParser(),
            new CommandResolver(),
            new RuntimeCommandEnvironmentDetector(),
            new OperationKindDetector(),
            badPolicy.Object,
            CreateOperationPolicyEngine(new CommandSafetyDecision(
                CommandSafetyDecisionType.Allow,
                CommandIntent.SafeReadOnly,
                0.99,
                [])));

        var result = await simulator.SimulateAsync("qualquer comando");

        Assert.Equal("error", result.ClassificationSource);
        Assert.Equal(CommandSafetyDecisionType.AskApproval, result.Decision);
    }
}