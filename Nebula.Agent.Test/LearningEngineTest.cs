using Moq;

using Nebula.Agent.Application;
using Nebula.Core.Commands;
using Nebula.Core.Learning;
using Nebula.Core.Safety;
using Nebula.Runner;
using Nebula.Services.Commands;
using Nebula.Services.Learning;
using Nebula.Services.Safety;

namespace Nebula.Agent.Test;

public sealed class LearningEngineTest
{
    [Fact]
    public async Task learning_without_web_provider_must_use_manual_seeds()
    {
        var engine = new LearningEngine(
            new DisabledWebResearchService(),
            new KnowledgeExtractor(),
            new KnowledgeClassificationPipeline(
                Path.Combine(
                    Path.GetTempPath(),
                    $"missing-knowledge-{Guid.NewGuid():N}.zip")),
            new InMemoryKnowledgeStore(),
            new Mock<ISafeExperimentRunner>().Object,
            new KnowledgeScoreEngine(),
            new Mock<ILogger>().Object);

        var report = await engine.LearnAsync(
            new LearningRequest(
                "Python basico",
                KnowledgeDomain.Python));

        Assert.True(report.Success, report.Error);
        Assert.True(report.DocumentsFound > 0);
        Assert.True(report.CreatedCount > 0);
        Assert.Contains(
            report.Warnings!,
            warning => warning.Contains(
                "Web research provider is not configured",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(KnowledgeDomain.Physics)]
    [InlineData(KnowledgeDomain.Chemistry)]
    public async Task physical_world_knowledge_must_be_not_testable_locally(
        KnowledgeDomain domain)
    {
        var runner = CreateExperimentRunner();
        var experiment = await runner.TryVerifyAsync(
            new KnowledgeItem
            {
                Domain = domain,
                Kind = KnowledgeItemKind.Concept,
                Topic = "basic concept",
                Title = "Concept",
                Content = "A real-world concept."
            },
            CancellationToken.None);

        Assert.Equal(
            VerificationKind.NotTestableLocally,
            experiment.VerificationKind);
        Assert.False(experiment.Success);
    }

    [Fact]
    public async Task safe_command_knowledge_can_be_verified_in_controlled_temp()
    {
        var executor = new Mock<IShellExecutor>();
        var resolvedExecutor = executor.As<IResolvedCommandExecutor>();
        resolvedExecutor
            .Setup(value => value.RunCommandDetailedAsync(
                It.IsAny<ResolvedCommand>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedCommand command, CancellationToken _) =>
                new ShellCommandResult
                {
                    Command = command.DisplayCommand,
                    WorkingDirectory = command.WorkingDirectory,
                    StandardOutput = "verified",
                    ExitCode = 0
                });
        var runner = CreateExperimentRunner(executor);

        var experiment = await runner.TryVerifyAsync(
            new KnowledgeItem
            {
                Domain = KnowledgeDomain.WindowsCommands,
                Kind = KnowledgeItemKind.Command,
                Topic = "list directory",
                Title = "List directory",
                Content = "Lists files.",
                NormalizedCommand = "dir"
            },
            CancellationToken.None);

        Assert.Equal(
            VerificationKind.SafeExecution,
            experiment.VerificationKind);
        Assert.True(experiment.Success);
        Assert.Equal("verified", experiment.StdOut);
    }

    [Fact]
    public void low_score_knowledge_must_not_be_used_automatically()
    {
        var item = new KnowledgeItem { FinalScore = 0.74 };

        Assert.False(
            new KnowledgeAutomationPolicy().CanUseAutomatically(item));
    }

    [Fact]
    public void score_engine_must_use_documented_weights()
    {
        var item = new KnowledgeItem
        {
            SourceScore = 1,
            ClassificationConfidence = 0.8,
            SafetyScore = 0.5,
            VerificationScore = 1
        };

        var score = new KnowledgeScoreEngine().Calculate(item);

        Assert.Equal(0.86, score, precision: 2);
    }

    private static SafeExperimentRunner CreateExperimentRunner(
        Mock<IShellExecutor>? executor = null)
    {
        var commandPolicy = new Mock<ICommandPolicyEngine>();
        commandPolicy
            .Setup(value => value.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandSafetyDecision(
                CommandSafetyDecisionType.Allow,
                CommandIntent.SafeExecuteLocal,
                1,
                ["Allowed by test policy."]));

        return new SafeExperimentRunner(
            (executor ?? new Mock<IShellExecutor>()).Object,
            commandPolicy.Object,
            new CommandIntentParser(),
            new CommandResolver(),
            new RuntimeCommandEnvironmentDetector(),
            new ScriptContentSafetyClassifier());
    }
}
