using System.Text.Json;

using Moq;

using Nebula.Agent.Application;
using Nebula.Agent.Domain;
using Nebula.Core.Configuration;
using Nebula.Core.Interactions;
using Nebula.Core.Learning;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Runner;
using Nebula.Services.Safety;

namespace Nebula.Agent.Test.Learning;

/// <summary>
/// Integration test that exercises the full agent loop with a real shell executor.
/// Mocks only the LLM (decisions + correctness verification).
/// Uses temp directories to avoid side effects.
/// </summary>
public sealed class FullFlowIntegrationTest : IDisposable
{
    private readonly string _testDir;

    public FullFlowIntegrationTest()
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            "Nebula",
            $"int_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task create_file_and_verify_it_exists()
    {
        var testFile = Path.Combine(_testDir, "hello.py");
        var decisions = new Queue<string>();

        // Decision 1: Write the file
        decisions.Enqueue(StructuredActionDecision(
            "Vou criar o script Python hello.py",
            "Criar o arquivo hello.py com print('Hello from Nebula')",
            OperationKind.FileWrite,
            content: "print('Hello from Nebula')",
            targetPath: testFile,
            language: "python"));

        // Decision 2: Verify file exists (auto-verify will also check)
        decisions.Enqueue(StructuredActionDecision(
            "Vou verificar se o arquivo foi criado",
            "Verificar existencia do arquivo",
            OperationKind.TerminalCommand,
            command: $"powershell.exe -Command \"Test-Path -LiteralPath '{testFile}'\""));

        // Decision 3: Complete
        decisions.Enqueue(CompleteDecision(
            "Tarefa concluida com sucesso",
            "Arquivo hello.py criado e verificado."));

        var llamaMock = CreateLlamaMock(decisions);
        var runner = CreateRunner(llamaMock);

        var request = new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "criar um script python hello world no diretorio de teste",
            Mode = InteractionMode.Agent,
            MaxSteps = 5,
            MaxRetriesPerStep = 2
        };

        var progress = new Progress<ConversationTurn>(_ => { });
        var result = await runner.RunAsync(request, progress);

        Assert.NotNull(result);
        Assert.NotEqual(ActionExecutionStatus.Unsafe, result.ActionStatus);
        Assert.NotEqual(ActionExecutionStatus.Failed, result.ActionStatus);

        // Verify file actually exists
        Assert.True(File.Exists(testFile),
            $"Expected file {testFile} to exist after agent execution");

        // Verify file content
        var content = await File.ReadAllTextAsync(testFile);
        Assert.Contains("Hello from Nebula", content);

        // Verify evidence was collected
        Assert.NotEmpty(result.Evidence);
        Assert.Contains(result.Evidence, e => e.Success);
    }

    [Fact]
    public async Task list_directory_with_correct_drive()
    {
        var decisions = new Queue<string>();

        decisions.Enqueue(StructuredActionDecision(
            "Vou listar os arquivos na raiz do D",
            "Listar diretorio raiz do drive D",
            OperationKind.TerminalCommand,
            command: "powershell.exe -Command \"Get-ChildItem -Path D:\\\""));

        decisions.Enqueue(CompleteDecision(
            "Listagem concluida",
            "Diretorio D: listado com sucesso."));

        var llamaMock = CreateLlamaMock(decisions);
        var runner = CreateRunner(llamaMock);

        var request = new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "listar arquivos na raiz do D",
            Mode = InteractionMode.Agent,
            MaxSteps = 3,
            MaxRetriesPerStep = 2
        };

        var progress = new Progress<ConversationTurn>(_ => { });
        var result = await runner.RunAsync(request, progress);

        Assert.NotNull(result);
        Assert.NotEqual(ActionExecutionStatus.Unsafe, result.ActionStatus);
        Assert.NotEqual(ActionExecutionStatus.Failed, result.ActionStatus);

        // Verify evidence was collected
        Assert.NotEmpty(result.Evidence);
        var evidence = result.Evidence.First(e => e.Success);
        Assert.NotNull(evidence);
    }

    [Fact]
    public async Task create_folder_and_verify()
    {
        var testSubDir = Path.Combine(_testDir, "minha_pasta");
        var decisions = new Queue<string>();

        // Decision 1: Create the folder
        decisions.Enqueue(StructuredActionDecision(
            "Vou criar a pasta minha_pasta",
            "Criar diretorio minha_pasta",
            OperationKind.TerminalCommand,
            command: $"powershell.exe -Command \"New-Item -ItemType Directory -Path '{testSubDir}' -Force\""));

        // Decision 2: Verify it exists
        decisions.Enqueue(StructuredActionDecision(
            "Vou verificar se a pasta foi criada",
            "Verificar existencia da pasta",
            OperationKind.TerminalCommand,
            command: $"powershell.exe -Command \"Test-Path -LiteralPath '{testSubDir}'\""));

        // Decision 3: Complete
        decisions.Enqueue(CompleteDecision(
            "Pasta criada e verificada",
            "Pasta minha_pasta criada com sucesso."));

        var llamaMock = CreateLlamaMock(decisions);
        var runner = CreateRunner(llamaMock);

        var request = new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = "criar uma pasta chamada minha_pasta",
            Mode = InteractionMode.Agent,
            MaxSteps = 5,
            MaxRetriesPerStep = 2
        };

        var progress = new Progress<ConversationTurn>(_ => { });
        var result = await runner.RunAsync(request, progress);

        Assert.NotNull(result);
        Assert.NotEqual(ActionExecutionStatus.Unsafe, result.ActionStatus);

        // Verify folder exists
        Assert.True(Directory.Exists(testSubDir),
            $"Expected directory {testSubDir} to exist after agent execution");
        Assert.NotEmpty(result.Evidence);
    }

    private static Mock<ILlamaClient> CreateLlamaMock(Queue<string> decisions)
    {
        var mock = new Mock<ILlamaClient>();
        mock.SetupGet(c => c.SelectedModel).Returns("test-model");

        // Decision prompt
        mock.Setup(c => c.GetStructuredResponseAsync(
                It.Is<string>(p => p.Contains("task execution agent")),
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => decisions.Dequeue());

        // Correctness verification (always "Yes" for test)
        mock.Setup(c => c.GetResponseAsync(
                It.Is<string>(p => p.Contains("Response only with")),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Yes");

        return mock;
    }

    private static AgentActionRunner CreateRunner(Mock<ILlamaClient> llamaMock)
    {
        var policy = CreateRealPolicyEngine();
        return new AgentActionRunner(
            llamaMock.Object,
            new ShellExecutor(),
            new JsonExtractor(),
            new Mock<ILogger>().Object,
            commandPolicyEngine: policy);
    }

    private static ICommandPolicyEngine CreateRealPolicyEngine()
    {
        var deterministic = new DeterministicCommandClassifier();
        var ml = new MlNetCommandClassifier(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"));
        return new CommandPolicyEngine(
            new CompositeCommandClassifier(deterministic, ml));
    }

    private static string StructuredActionDecision(
        string reasoningSummary,
        string objective,
        OperationKind operationKind,
        string command = "",
        string? content = null,
        string? targetPath = null,
        string? language = null)
    {
        return JsonSerializer.Serialize(new AgentActionDecision
        {
            ReasoningSummary = reasoningSummary,
            Action = new AgentToolAction
            {
                Objective = objective,
                OperationKind = operationKind,
                Command = command,
                Content = content,
                TargetPath = targetPath,
                Language = language,
                RequiresSafetyReview = true
            }
        });
    }

    private static string CompleteDecision(
        string reasoningSummary,
        string completionMessage)
    {
        return JsonSerializer.Serialize(new AgentActionDecision
        {
            ReasoningSummary = reasoningSummary,
            IsComplete = true,
            CompletionMessage = completionMessage
        });
    }
}
