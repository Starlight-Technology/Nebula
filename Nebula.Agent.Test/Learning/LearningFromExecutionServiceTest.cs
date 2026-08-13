using Moq;

using Nebula.Agent.Application;
using Nebula.Core.Learning;
using Nebula.Services.Learning;

namespace Nebula.Agent.Test.Learning;

public sealed class LearningFromExecutionServiceTest
{
    private const string TestSessionId = "11111111-1111-1111-1111-111111111111";
    private const string TestStepId = "22222222-2222-2222-2222-222222222222";
    private static readonly Guid SessionGuid = Guid.Parse(TestSessionId);
    private static readonly Guid StepGuid = Guid.Parse(TestStepId);

    [Fact]
    public async Task create_folder_command_is_learned_as_knowledge()
    {
        var store = new InMemoryKnowledgeStore();
        var service = CreateService(store);

        await service.RecordSuccessfulCommandAsync(
            "New-Item -ItemType Directory -Path C:\\temp\\test-folder",
            "New-Item -ItemType Directory -Path C:\\temp\\test-folder",
            "C:\\temp",
            0,
            "Directory: C:\\temp\\test-folder",
            "",
            SessionGuid,
            StepGuid);

        var details = await store.FindDetailsAsync(
            "New-Item",
            minimumScore: 0,
            cancellationToken: CancellationToken.None);

        var item = Assert.Single(details);
        Assert.Equal(KnowledgeItemKind.Command, item.Item.Kind);
        Assert.True(item.Item.FinalScore >= 0.75);
        Assert.True(item.Item.IsValidated);
        Assert.Equal("Learned from successful agent execution.", item.Item.ValidationNotes);

        var experiment = Assert.Single(item.Experiments);
        Assert.True(experiment.Success);
        Assert.Equal(VerificationKind.SafeExecution, experiment.VerificationKind);
        Assert.Equal(0, experiment.ExitCode);
        Assert.Contains("New-Item", experiment.CommandExecuted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task list_directory_command_is_learned_as_knowledge()
    {
        var store = new InMemoryKnowledgeStore();
        var service = CreateService(store);

        await service.RecordSuccessfulCommandAsync(
            "Get-ChildItem -Path C:\\temp",
            "Get-ChildItem -Path C:\\temp",
            "C:\\temp",
            0,
            "Mode LastWriteTime Length Name\n---- ------------- ------ ----\nd----  2024-01-01 folder1\n-a---  2024-01-02 file.txt",
            "",
            SessionGuid,
            StepGuid);

        var details = await store.FindDetailsAsync(
            "Get-ChildItem",
            minimumScore: 0,
            cancellationToken: CancellationToken.None);

        var item = Assert.Single(details);
        Assert.Equal(KnowledgeItemKind.Command, item.Item.Kind);
        Assert.True(item.Item.FinalScore >= 0.75);

        var experiment = Assert.Single(item.Experiments);
        Assert.True(experiment.Success);
        Assert.Equal(0, experiment.ExitCode);
        Assert.Contains("folder1", experiment.StdOut, StringComparison.Ordinal);
        Assert.Contains("file.txt", experiment.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task python_script_creation_is_learned_as_file_operation()
    {
        var store = new InMemoryKnowledgeStore();
        var service = CreateService(store);

        var scriptContent = "print('hello world')";
        var scriptHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(scriptContent)));

        await service.RecordSuccessfulFileOperationAsync(
            "ScriptContent",
            "C:\\temp\\hello.py",
            scriptHash,
            SessionGuid,
            StepGuid);

        var details = await store.FindDetailsAsync(
            "hello.py",
            minimumScore: 0,
            cancellationToken: CancellationToken.None);

        var item = Assert.Single(details);
        Assert.Equal(KnowledgeItemKind.Procedure, item.Item.Kind);
        Assert.True(item.Item.FinalScore >= 0.75);
        Assert.Contains("ScriptContent", item.Item.Content, StringComparison.Ordinal);

        var experiment = Assert.Single(item.Experiments);
        Assert.True(experiment.Success);
        Assert.Equal(0, experiment.ExitCode);
    }

    [Fact]
    public async Task successful_command_is_relearned_and_score_increases()
    {
        var store = new InMemoryKnowledgeStore();
        var service = CreateService(store);

        await service.RecordSuccessfulCommandAsync(
            "dotnet --version",
            "dotnet --version",
            "C:\\project",
            0,
            "10.0.301",
            "",
            SessionGuid,
            StepGuid);

        var firstDetails = await store.FindDetailsAsync(
            "dotnet", minimumScore: 0, cancellationToken: CancellationToken.None);
        var firstScore = firstDetails.Single().Item.FinalScore;

        await service.RecordSuccessfulCommandAsync(
            "dotnet --version",
            "dotnet --version",
            "C:\\project",
            0,
            "10.0.301",
            "",
            SessionGuid,
            StepGuid);

        var secondDetails = await store.FindDetailsAsync(
            "dotnet", minimumScore: 0, cancellationToken: CancellationToken.None);
        var secondItem = secondDetails.Single().Item;

        Assert.True(secondItem.FinalScore >= firstScore,
            $"Expected score to increase or stay same: {secondItem.FinalScore} >= {firstScore}");
        Assert.True(secondItem.ObservationCount >= 2,
            $"Expected observation count >= 2, got {secondItem.ObservationCount}");
    }

    [Fact]
    public async Task failed_command_is_stored_as_warning_with_error_category()
    {
        var store = new InMemoryKnowledgeStore();
        var service = CreateService(store);

        await service.RecordFailedCommandAsync(
            "invalid-command --unknown",
            "invalid-command --unknown",
            "C:\\temp",
            -1,
            "",
            "'invalid-command' is not recognized as an internal or external command",
            "CommandNotFound",
            SessionGuid,
            StepGuid);

        var details = await store.FindDetailsAsync(
            "invalid-command",
            minimumScore: 0,
            cancellationToken: CancellationToken.None);

        var item = Assert.Single(details);
        Assert.Equal(KnowledgeItemKind.Warning, item.Item.Kind);
        Assert.True(item.Item.FinalScore < 0.75,
            $"Expected low score for failed command, got {item.Item.FinalScore}");
        Assert.Contains("CommandNotFound", string.Join(",", item.Experiments.Select(e => e.ErrorCategory)),
            StringComparison.Ordinal);
        Assert.False(item.Item.IsValidated);

        var experiment = Assert.Single(item.Experiments);
        Assert.False(experiment.Success);
        Assert.Equal(-1, experiment.ExitCode);
        Assert.Equal("CommandNotFound", experiment.ErrorCategory);
        Assert.Contains("not recognized", experiment.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task knowledge_is_queryable_after_learning_from_execution()
    {
        var store = new InMemoryKnowledgeStore();
        var logger = new Mock<ILogger>();
        var service = CreateService(store);

        await service.RecordSuccessfulCommandAsync(
            "python --version",
            "python --version",
            "C:\\temp",
            0,
            "Python 3.12.0",
            "",
            SessionGuid,
            StepGuid);

        var queryService = new KnowledgeQueryService(store, logger.Object);
        var answer = await queryService.AnswerAsync(
            "python",
            CancellationToken.None);

        Assert.Contains("python --version", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exit code 0", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Score", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sucesso=True", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task multiple_operations_are_learned_independently()
    {
        var store = new InMemoryKnowledgeStore();
        var service = CreateService(store);

        await service.RecordSuccessfulCommandAsync(
            "mkdir test-dir",
            "mkdir test-dir",
            "/home/user",
            0,
            "",
            "",
            SessionGuid, StepGuid);

        await service.RecordSuccessfulCommandAsync(
            "ls -la",
            "ls -la",
            "/home/user",
            0,
            "drwxr-xr-x 2 user user 4096 Jan 1 12:00 test-dir",
            "",
            SessionGuid, StepGuid);

        await service.RecordSuccessfulFileOperationAsync(
            "FileWrite",
            "/home/user/hello.py",
            "abc123",
            SessionGuid, StepGuid);

        var allDetails = await store.FindDetailsAsync(
            "", minimumScore: 0, cancellationToken: CancellationToken.None);

        Assert.Equal(3, allDetails.Count);
        Assert.Contains(allDetails, d => d.Item.Title.Contains("mkdir", StringComparison.Ordinal));
        Assert.Contains(allDetails, d => d.Item.Title.Contains("ls", StringComparison.Ordinal));
        Assert.Contains(allDetails, d => d.Item.Title.Contains("hello.py", StringComparison.Ordinal));
    }

    private static LearningFromExecutionService CreateService(IKnowledgeStore store)
    {
        var logger = new Mock<ILogger>();
        return new LearningFromExecutionService(
            store,
            new KnowledgeScoreEngine(),
            logger.Object);
    }
}
