using System.Text.Json;

using Moq;
using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Core.Agent;
using Nebula.Core.Configuration;
using Nebula.Core.Commands;
using Nebula.Core.Execution;
using Nebula.Core.Learning;
using Nebula.Core.Memory;
using Nebula.Core.Operations;
using Nebula.Core.Projects;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Runner;
using Nebula.Services.Projects;

namespace Nebula.Agent.Test;

public sealed class AgentActionRunnerTest
{
    [Fact]
    public async Task generate_next_step_async_must_include_chat_history_and_execution_context()
    {
        var decision = ActionDecision(
            "I need to create the script with the remembered project name.",
            "Create script",
            "echo Nebula > project.py");
        string? capturedPrompt = null;

        var llamaClientMock = CreateLlamaClientMock();
        llamaClientMock
            .Setup(client => client.GetStructuredResponseAsync(
                It.Is<string>(prompt => prompt.Contains("task execution agent", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?, CancellationToken>(
                (prompt, _, _) => capturedPrompt = prompt)
            .ReturnsAsync(decision);

        var runner = CreateRunner(llamaClientMock);

        var result = await runner.GenerateNextStepAsync(new AgentActionDecisionRequest
        {
            Objective = "Create a script that prints the previous project name.",
            ChatHistoryContext = "[recent_messages]\nuser: The project name is Nebula.",
            CurrentPlan = "1. Inspect project - completed.",
            PreviousActionResult = "Project inspected.",
            Observations = ["Step 1 succeeded."],
            StepNumber = 2,
            RetryNumber = 1
        });

        Assert.Equal("echo Nebula > project.py", result.Action?.Command);
        Assert.NotNull(capturedPrompt);
        Assert.Contains("The project name is Nebula", capturedPrompt);
        Assert.Contains("Project inspected", capturedPrompt);
        Assert.Contains("Step 1 succeeded", capturedPrompt);
        Assert.Contains("Step 2", capturedPrompt);
        Assert.Contains("Retry 1", capturedPrompt);
        Assert.Contains("Respond ONLY with valid JSON", capturedPrompt);
        Assert.Contains(
            "Output reasoningSummary and completionMessage in English only",
            capturedPrompt);
    }

    [Fact]
    public async Task run_async_must_complete_simple_react_objective_over_multiple_iterations()
    {
        var scriptDirectory = Path.Combine(
            Path.GetTempPath(),
            "Nebula",
            "tests",
            Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(scriptDirectory, "hello.py");
        var runCommand = $"python \"{scriptPath}\"";
        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to create the script before it can be executed.",
                "Create hello.py",
                OperationKind.ScriptContent,
                content: "print('hello world')",
                targetPath: scriptPath,
                language: "python"),
            StructuredActionDecision(
                "The script exists, so I need to run it and inspect the output.",
                "Run hello.py",
                OperationKind.ScriptExecution,
                command: runCommand,
                targetPath: scriptPath),
            CompleteDecision(
                "The observed output matches the requested result.",
                "Created and ran hello.py successfully.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(runCommand, It.IsAny<CancellationToken>()))
            .ReturnsAsync("hello world");

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(
            CreateRequest("Create a Python script that prints hello world and run it."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal("Created and ran hello.py successfully.", result.Response);
        Assert.Equal(2, result.Commands.Count);
        Assert.True(File.Exists(scriptPath));
        Assert.Equal(2, result.Evidence.Count);
        Assert.Contains(
            result.Evidence,
            evidence =>
                evidence.OperationKind == OperationKind.ScriptContent &&
                evidence.Success &&
                evidence.FilePath == scriptPath);
        Assert.Contains(
            result.Evidence,
            evidence =>
                evidence.OperationKind == OperationKind.ScriptExecution &&
                evidence.Success &&
                evidence.StdOut == "hello world");
        Assert.Equal(
            2,
            result.ActionEvents.Count(actionEvent => actionEvent.Kind == ActionExecutionEventKind.ActionStarted));
        Assert.Equal(
            2,
            result.ActionEvents.Count(actionEvent => actionEvent.Kind == ActionExecutionEventKind.ActionCompleted));
        Assert.Equal(
            2,
            result.ActionEvents.Count(actionEvent => actionEvent.Kind == ActionExecutionEventKind.Observation));
        Assert.Equal(ActionExecutionEventKind.Completed, result.ActionEvents.Last().Kind);
        executorMock.Verify(
            executor => executor.RunCommandAsync(runCommand, It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.NotNull(result.FinalReport);
        Assert.Contains("Relatorio final", result.FinalReport);
        Assert.Contains("Arquivos alterados (1)", result.FinalReport);
        Assert.Contains(scriptPath, result.FinalReport);
        Assert.Matches(@"Comandos executados \(\d+\)", result.FinalReport);
        Assert.Contains("Nenhum risco identificado", result.FinalReport);
    }

    [Fact]
    public async Task run_async_must_persist_run_and_steps_when_store_is_available()
    {
        var scriptDirectory = Path.Combine(
            Path.GetTempPath(),
            "Nebula",
            "tests",
            Guid.NewGuid().ToString("N"));
        var scriptPath = Path.Combine(scriptDirectory, "hello.py");
        var runCommand = $"python \"{scriptPath}\"";
        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to create the script before it can be executed.",
                "Create hello.py",
                OperationKind.ScriptContent,
                content: "print('hello world')",
                targetPath: scriptPath,
                language: "python"),
            CompleteDecision(
                "The script was created successfully.",
                "Created hello.py.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var executorMock = CreateExecutorMock();
        var runStore = new FakeAgentRunStore();

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: executorMock,
            agentRunStore: runStore);
        var result = await runner.RunAsync(
            CreateRequest("Create a Python script that prints hello world."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal(2, runStore.SavedRuns.Count);
        var checkpoint = runStore.SavedRuns[0];
        Assert.Equal(result.RequestId, checkpoint.Id);
        Assert.Null(checkpoint.FinishedAt);
        Assert.False(checkpoint.IsCancelled);
        var run = runStore.SavedRuns[1];
        Assert.Equal(result.RequestId, run.Id);
        Assert.Equal(result.RequestId, run.RequestId);
        Assert.Equal("Completed", run.Status);
        Assert.Equal("qwen3:8b", run.ModelName);
        Assert.False(run.IsCancelled);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal(run.CurrentPlan, checkpoint.CurrentPlan);
        var step = Assert.Single(run.Steps);
        Assert.Equal(OperationKind.ScriptContent, step.OperationKind);
        Assert.Contains("hello.py", step.Command);
        Assert.True(step.Success);
    }

    [Fact]
    public async Task run_async_must_not_fail_when_run_store_throws()
    {
        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            "Nebula",
            "tests",
            Guid.NewGuid().ToString("N"),
            "hello.py");
        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to create the script.",
                "Create hello.py",
                OperationKind.ScriptContent,
                content: "print('hello world')",
                targetPath: scriptPath,
                language: "python"),
            CompleteDecision(
                "The script was created.",
                "Done.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var runStore = new FakeAgentRunStore { ThrowOnSave = true };

        var runner = CreateRunner(llamaClientMock, agentRunStore: runStore);
        var result = await runner.RunAsync(
            CreateRequest("Create a Python script."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
    }

    private sealed class FakeAgentRunStore : IAgentRunStore
    {
        public bool ThrowOnSave { get; set; }

        public List<AgentRun> SavedRuns { get; } = [];

        public Task SaveRunAsync(
            AgentRun run,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave)
            {
                throw new InvalidOperationException("store unavailable");
            }

            SavedRuns.Add(run);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentRun>> GetRunsAsync(
            int limit = 20,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentRun>>(SavedRuns);
        }

        public Task<AgentRun?> GetRunAsync(
            Guid runId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SavedRuns.SingleOrDefault(run => run.Id == runId));
        }

        public Task<IReadOnlyList<AgentRun>> GetUnfinishedRunsAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AgentRun>>(
                SavedRuns.Where(run => run.ConversationId == conversationId && run.FinishedAt is null)
                    .ToList());
        }
    }

    [Fact]
    public async Task run_async_must_pass_chat_history_to_every_decision()
    {
        var capturedPrompts = new List<string>();
        var decisions = new Queue<string>(
        [
            ActionDecision(
                "I need to create the script using the remembered project name.",
                "Create project script",
                "echo Nebula > project.py"),
            CompleteDecision(
                "The file creation observation confirms the objective is complete.",
                "Created project.py.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        llamaClientMock
            .Setup(client => client.GetStructuredResponseAsync(
                It.Is<string>(prompt => prompt.Contains("task execution agent", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?, CancellationToken>(
                (prompt, _, _) => capturedPrompts.Add(prompt))
            .ReturnsAsync(() => decisions.Dequeue());
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("echo Nebula > project.py", It.IsAny<CancellationToken>()))
            .ReturnsAsync("created");

        var runner = CreateRunner(llamaClientMock, executorMock);
        var request = CreateRequest("Create a script that prints that project name.");
        request.ChatHistoryContext = "[recent_messages]\nuser: The project name is Nebula.";

        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal(2, capturedPrompts.Count);
        Assert.All(capturedPrompts, prompt => Assert.Contains("The project name is Nebula", prompt));
        Assert.Contains("created", capturedPrompts[1]);
    }

    [Fact]
    public async Task run_async_must_invoke_existing_terminal_tool_and_log_action_and_observation()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need to inspect the current directory.", "List files", "dir"),
            CompleteDecision("The directory listing was returned.", "Directory inspected.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                It.Is<string>(command => command.Contains("Get-ChildItem", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Directory listing");

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(CreateRequest("List files in the current directory."), progress: null);

        Assert.Contains(
            result.ActionEvents,
            actionEvent =>
                actionEvent.Kind == ActionExecutionEventKind.ActionStarted &&
                actionEvent.Command!.Contains("Get-ChildItem", StringComparison.Ordinal));
        Assert.Contains(
            result.ActionEvents,
            actionEvent =>
                actionEvent.Kind == ActionExecutionEventKind.Observation &&
                actionEvent.ToolResponse == "Directory listing");
        executorMock.Verify(
            executor => executor.RunCommandAsync(
                It.Is<string>(command => command.Contains("Get-ChildItem", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_not_claim_execution_without_evidence()
    {
        var decisions = new Queue<string>(
        [
            CompleteDecision(
                "I think the task is complete.",
                "Executed successfully."),
            ActionDecision(
                "The previous completion claim was rejected, so I must actually execute something.",
                "Run a safe command",
                "echo evidence"),
            CompleteDecision(
                "The command executed and produced real output.",
                "Executed successfully.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("echo evidence", It.IsAny<CancellationToken>()))
            .ReturnsAsync("evidence");

        var result = await CreateRunner(llamaClientMock, executorMock).RunAsync(
            CreateRequest("Execute something."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.NotEmpty(result.Evidence);
        Assert.Contains(
            result.ActionEvents,
            ev => (ev.Message ?? string.Empty).Contains(
                "claimed the task is complete",
                StringComparison.OrdinalIgnoreCase));
        executorMock.Verify(
            executor => executor.RunCommandAsync("echo evidence", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_route_learning_request_without_inventing_sources()
    {
        var llamaClientMock = CreateLlamaClientMock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt =>
                    prompt.Contains("Extract structured knowledge", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<string?>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                """
                {
                  "items": [
                    {
                      "sourceUrl": "nebula://manual-seed/python-launcher-windows",
                      "evidenceSummary": "py can work when python is not on PATH.",
                      "confidence": 0.94,
                      "domain": "Python",
                      "kind": "Command",
                      "title": "Python Launcher no Windows",
                      "content": "No Windows, py pode funcionar quando python nao esta no PATH.",
                      "summary": "No Windows, py pode funcionar quando python nao esta no PATH.",
                      "examples": ["py --version"],
                      "warnings": [],
                      "facts": ["py --version verifica o Python Launcher."],
                      "normalizedCommand": "py --version",
                      "language": "cmd",
                      "executableLocally": true
                    }
                  ]
                }
                """);

        var result = await CreateRunner(llamaClientMock).RunAsync(
            CreateRequest("aprenda Python basico"),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Contains("Aprendi", result.Response);
        Assert.Contains(
            "Web research provider is not configured",
            result.Response);
        Assert.Contains("Python", result.Response);
        llamaClientMock.Verify(
            client => client.GetResponseAsync(
                It.Is<string>(prompt =>
                    prompt.Contains("Extract structured knowledge", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<string?>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task run_async_must_summarize_many_learned_items()
    {
        var llamaClientMock = CreateLlamaClientMock();
        var items = Enumerable.Range(1, 25)
            .Select(index => new KnowledgeItem
            {
                Domain = KnowledgeDomain.WindowsCommands,
                Kind = KnowledgeItemKind.Command,
                Title = $"CMD: Cmd{index}",
                Content = $"O comando cmd{index} faz algo util.",
                Summary = $"O comando cmd{index} faz algo util.",
                Tags = "cmd,windows,command-reference",
                NormalizedCommand = $"cmd{index}",
                SourceUrl = "file:///comandos-cmd.txt",
                SourceType = LearningSourceType.LocalFile,
                SourceName = "LearningSourceReader",
                RiskLevel = KnowledgeRiskLevel.Unknown,
                FinalScore = 0.71
            })
            .ToList();
        var learningEngine = new Mock<ILearningEngine>();
        learningEngine
            .Setup(engine => engine.LearnAsync(
                It.IsAny<LearningRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LearningReport(
                true,
                null,
                items,
                [
                    new KnowledgeSource
                    {
                        ProviderName = "LearningSourceReader",
                        Publisher = "LearningSourceReader",
                        SourceType = LearningSourceType.LocalFile,
                        Url = "file:///comandos-cmd.txt",
                        Title = "comandos-cmd.txt"
                    }
                ],
                [],
                [],
                CreatedCount: items.Count,
                DocumentsFound: 1));

        var result = await CreateRunner(
            llamaClientMock,
            learningEngine: learningEngine.Object).RunAsync(
                CreateRequest("aprenda comandos cmd"),
                progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Contains("Resumo do que aprendi: 25 comandos", result.Response);
        Assert.Contains("Exemplos de comandos aprendidos: cmd1", result.Response);
        Assert.Contains("Mostrando 20 de 25 itens aprendidos", result.Response);
        Assert.Contains("Mais 5 itens ficaram salvos", result.Response);
    }

    [Fact]
    public async Task run_async_must_answer_knowledge_question_from_store_without_llm()
    {
        var llamaClientMock = CreateLlamaClientMock();
        var queryService = new Mock<IKnowledgeQueryService>();
        queryService
            .Setup(service => service.AnswerAsync(
                "Get-ChildItem",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                "Get-ChildItem lists files. Fonte: https://learn.microsoft.com/powershell/");
        var runner = CreateRunner(
            llamaClientMock,
            knowledgeQueryService: queryService.Object);

        var result = await runner.RunAsync(
            CreateRequest("O que você sabe sobre Get-ChildItem?"),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Contains("learn.microsoft.com", result.Response);
        queryService.Verify(
            service => service.AnswerAsync(
                "Get-ChildItem",
                It.IsAny<CancellationToken>()),
            Times.Once);
        llamaClientMock.Verify(
            client => client.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_log_environment_resolution_and_policy_before_execution()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need to inspect drive D.", "List drive D", "ls D:"),
            CompleteDecision("The listing was returned.", "Drive inspected.")
        ]);
        var logs = new List<string>();
        var loggerMock = CreateLoggerMock();
        loggerMock
            .Setup(logger => logger.Log(It.IsAny<string>()))
            .Callback<string>(logs.Add);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                It.Is<string>(command => command.Contains("Get-ChildItem", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Directory listing");

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            loggerMock: loggerMock);
        await runner.RunAsync(
            CreateRequest("Listar arquivos da unidade D."),
            progress: null);

        var entry = Assert.Single(
            logs,
            log => log.StartsWith(
                "[AGENT] Command execution decision:",
                StringComparison.Ordinal));
        Assert.Contains("os=", entry);
        Assert.Contains("shell=", entry);
        Assert.Contains("userText=", entry);
        Assert.Contains("resolvedCommand=", entry);
        Assert.Contains("workingDirectory=", entry);
        Assert.Contains("policyDecision=", entry);
        Assert.Contains("policyReasons=", entry);
    }

    [Fact]
    public async Task run_async_must_retry_failed_step_with_observation_until_success()
    {
        var capturedPrompts = new List<string>();
        var decisions = new Queue<string>(
        [
            ActionDecision("I need to run the requested command.", "Run command", "badcmd"),
            ActionDecision("The first command failed, so I need to use the corrected command.", "Run corrected command", "goodcmd"),
            CompleteDecision("The corrected command succeeded.", "Objective completed.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        llamaClientMock
            .Setup(client => client.GetStructuredResponseAsync(
                It.Is<string>(prompt => prompt.Contains("task execution agent", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?, CancellationToken>(
                (prompt, _, _) => capturedPrompts.Add(prompt))
            .ReturnsAsync(() => decisions.Dequeue());
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bad command"));
        executorMock
            .Setup(executor => executor.RunCommandAsync("goodcmd", It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(CreateRequest("Create a script file and run it."), progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Contains(
            result.ActionEvents,
            actionEvent =>
                actionEvent.Kind == ActionExecutionEventKind.RetryScheduled &&
                actionEvent.Step == 1);
        Assert.Contains(result.Commands, command => command.Attempt == 1 && command.Error == "bad command");
        Assert.Contains(result.Commands, command => command.Attempt == 2 && command.Executed);
        Assert.Contains("bad command", capturedPrompts[1]);
        Assert.Contains("Retry 1", capturedPrompts[1]);
    }

    [Fact]
    public async Task run_async_must_record_invalid_command_and_use_error_reflection_without_repeating_it()
    {
        const string failedCommand = "invalid_command_12345";
        const string diagnosticCommand = "where invalid_command_12345";
        string? reflectionPrompt = null;
        var decisionPrompts = new List<string>();
        var decisions = new Queue<string>(
        [
            ActionDecision(
                "I need to execute the requested command.",
                "Run invalid command",
                failedCommand),
            CompleteDecision(
                "The diagnostic command completed.",
                "Diagnostic completed.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        llamaClientMock
            .Setup(client => client.GetStructuredResponseAsync(
                It.Is<string>(prompt => prompt.Contains("task execution agent", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?, CancellationToken>(
                (prompt, _, _) => decisionPrompts.Add(prompt))
            .ReturnsAsync(() => decisions.Dequeue());
        llamaClientMock
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ErrorReflectionStep", StringComparison.Ordinal)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IProgress<LlamaStreamUpdate>?, CancellationToken>(
                (prompt, _, _) => reflectionPrompt = prompt)
            .ReturnsAsync(ErrorReflectionDecision(
                "The executable is not installed or is not on PATH.",
                "Check whether the executable is available on PATH",
                diagnosticCommand));
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateDetailedExecutorMock();
        var detailedExecutor = executorMock.As<IDetailedShellExecutor>();
        detailedExecutor
            .Setup(executor => executor.RunCommandDetailedAsync(
                failedCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedResult(
                failedCommand,
                "'invalid_command_12345' is not recognized as an internal or external command.",
                9009));
        detailedExecutor
            .Setup(executor => executor.RunCommandDetailedAsync(
                diagnosticCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(diagnosticCommand, "not found"));

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(
            CreateRequest("Run an invalid command and diagnose the failure."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal(2, result.ExecutionHistory.Count);
        var failure = result.ExecutionHistory[0];
        Assert.Equal(failedCommand, failure.Command);
        Assert.Equal(9009, failure.ExitCode);
        Assert.False(failure.Success);
        Assert.Contains("not recognized", failure.StandardError);
        Assert.NotEqual(default, failure.Timestamp);
        Assert.NotNull(reflectionPrompt);
        Assert.Contains("Exit code:", reflectionPrompt);
        Assert.Contains("9009", reflectionPrompt);
        Assert.Contains("not recognized", reflectionPrompt);
        Assert.Contains("Recent execution history", reflectionPrompt);
        Assert.Contains(
            "Qual hipótese explica esse erro e qual comando diferente deve ser tentado agora?",
            reflectionPrompt);
        Assert.Contains("History:", decisionPrompts[^1]);
        Assert.Contains("Failed", decisionPrompts[^1]);
        detailedExecutor.Verify(
            executor => executor.RunCommandDetailedAsync(
                failedCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        detailedExecutor.Verify(
            executor => executor.RunCommandDetailedAsync(
                diagnosticCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_diagnose_identity_after_permission_error()
    {
        const string failedCommand = "type protected.txt";
        const string diagnosticCommand = "whoami";
        var decisions = new Queue<string>(
        [
            ActionDecision(
                "I need to read the requested file.",
                "Read protected file",
                failedCommand),
            CompleteDecision(
                "The identity diagnostic was collected.",
                "Permission diagnostic completed.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        llamaClientMock
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ErrorReflectionStep", StringComparison.Ordinal)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorReflectionDecision(
                "The current user may not have access to the file or directory.",
                "Inspect the current user before changing permissions",
                diagnosticCommand));
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateDetailedExecutorMock();
        var detailedExecutor = executorMock.As<IDetailedShellExecutor>();
        detailedExecutor
            .Setup(executor => executor.RunCommandDetailedAsync(
                failedCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedResult(failedCommand, "Access is denied.", 5));
        detailedExecutor
            .Setup(executor => executor.RunCommandDetailedAsync(
                diagnosticCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(diagnosticCommand, "desktop-user"));

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(
            CreateRequest("Read a protected file."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal(diagnosticCommand, result.Commands[1].Run);
        Assert.Contains(
            result.ActionEvents,
            actionEvent =>
                actionEvent.Kind == ActionExecutionEventKind.ErrorReflection &&
                actionEvent.Command == diagnosticCommand);
        Assert.Contains(
            result.ActionEvents,
            actionEvent => actionEvent.Kind == ActionExecutionEventKind.PlanRevised);
        detailedExecutor.Verify(
            executor => executor.RunCommandDetailedAsync(
                failedCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_diagnose_missing_package_manager_without_repeating_install()
    {
        const string installCommand = "npm install";
        const string diagnosticCommand = "where npm";
        var decisions = new Queue<string>(
        [
            ActionDecision(
                "I need to install the project packages.",
                "Install packages",
                installCommand),
            CompleteDecision(
                "The package manager availability was diagnosed.",
                "Package manager diagnostic completed.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        llamaClientMock
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ErrorReflectionStep", StringComparison.Ordinal)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorReflectionDecision(
                "npm is missing or not available on PATH.",
                "Check whether npm is installed before attempting installation again",
                diagnosticCommand));
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateDetailedExecutorMock();
        var detailedExecutor = executorMock.As<IDetailedShellExecutor>();
        detailedExecutor
            .Setup(executor => executor.RunCommandDetailedAsync(
                installCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailedResult(
                installCommand,
                "'npm' is not recognized as an internal or external command.",
                9009));
        detailedExecutor
            .Setup(executor => executor.RunCommandDetailedAsync(
                diagnosticCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(diagnosticCommand, "npm was not found"));

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(
            CreateRequest("Install project packages."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal([installCommand, diagnosticCommand], result.ExecutionHistory.Select(x => x.Command));
        detailedExecutor.Verify(
            executor => executor.RunCommandDetailedAsync(
                installCommand,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_stop_after_three_similar_errors()
    {
        const string firstCommand = "read-protected";
        var reflections = new Queue<string>(
        [
            ErrorReflectionDecision(
                "The current user lacks permission.",
                "Inspect the current identity",
                "whoami"),
            ErrorReflectionDecision(
                "The shell cannot access the environment.",
                "Inspect the current directory",
                "cd"),
            ErrorReflectionDecision(
                "The environment still denies access.",
                "Stop and inspect directory permissions manually",
                "dir")
        ]);
        var decisions = new Queue<string>(
        [
            ActionDecision(
                "I need to access the protected resource.",
                "Read protected resource",
                firstCommand)
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        llamaClientMock
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ErrorReflectionStep", StringComparison.Ordinal)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => reflections.Dequeue());
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateDetailedExecutorMock();
        var detailedExecutor = executorMock.As<IDetailedShellExecutor>();
        detailedExecutor
            .Setup(executor => executor.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, string workingDirectory, CancellationToken _) =>
                FailedResult(command, "Permission denied.", 13, workingDirectory));

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(
            CreateRequest("Create a file after checking directory permissions."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Contains("mesmo erro ocorreu 3 vezes", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, result.ExecutionHistory.Count);
        Assert.Equal(3, result.ExecutionHistory.Count(entry => entry.ErrorSignature == "permission-denied"));
        detailedExecutor.Verify(
            executor => executor.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task run_async_must_stop_after_one_command_not_found_alternative()
    {
        const string firstCommand = "missing-tool";
        const string alternativeCommand = "where missing-tool";
        var decisions = new Queue<string>(
        [
            ActionDecision(
                "I need to run the requested tool.",
                "Run missing tool",
                firstCommand)
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        llamaClientMock
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ErrorReflectionStep", StringComparison.Ordinal)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorReflectionDecision(
                "The executable is not installed.",
                "Check the executable on PATH",
                alternativeCommand));
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateDetailedExecutorMock();
        var detailedExecutor = executorMock.As<IDetailedShellExecutor>();
        detailedExecutor
            .Setup(executor => executor.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, string workingDirectory, CancellationToken _) =>
                FailedResult(
                    command,
                    $"The term '{command}' is not recognized as the name of a cmdlet.",
                    1,
                    workingDirectory));

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(
            CreateRequest("Run a missing command."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Equal(2, result.ExecutionHistory.Count);
        Assert.All(
            result.ExecutionHistory,
            entry => Assert.Equal("command-not-found", entry.ErrorSignature));
        detailedExecutor.Verify(
            executor => executor.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task run_async_must_stop_when_retry_limit_is_reached()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need to run the command.", "Run command", "badcmd"),
            ActionDecision("The command failed, so I need to retry the same step.", "Retry command", "badcmd")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("still failing"));

        var request = CreateRequest("Create a script file and run it.");
        request.MaxRetriesPerStep = 1;

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Contains("Limite de retry por passo (1)", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, result.Commands.Count(command => command.Run == "badcmd"));
        Assert.Contains(
            result.ActionEvents,
            actionEvent => actionEvent.Kind == ActionExecutionEventKind.DeduplicationBlocked);
        Assert.Equal(ActionExecutionEventKind.Failed, result.ActionEvents.Last().Kind);
        executorMock.Verify(
            executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_stop_when_step_limit_is_reached()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need to create the script.", "Create script", "echo hello > hello.py"),
            ActionDecision("I still need to execute the script.", "Run script", "python hello.py")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("echo hello > hello.py", It.IsAny<CancellationToken>()))
            .ReturnsAsync("created");

        var request = CreateRequest("Create a script and run it.");
        request.MaxSteps = 1;

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Contains("limite de 1 passo", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Single(result.Commands);
        executorMock.Verify(
            executor => executor.RunCommandAsync("python hello.py", It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_request_approval_without_invoking_tool()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need a package.", "Install package", "pip install requests")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();
        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateRealPolicyEngine());

        var result = await runner.RunAsync(CreateRequest("Install requests."), progress: null);

        Assert.Equal(ActionExecutionStatus.AwaitingApproval, result.ActionStatus);
        Assert.Equal(ActionExecutionEventKind.ApprovalRequired, result.ActionEvents.Last().Kind);
        Assert.Contains("confirmação", result.Response, StringComparison.OrdinalIgnoreCase);
        executorMock.Verify(
            executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_execute_approval_command_when_auto_approval_is_enabled()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need a package.", "Install package", "pip install requests"),
            CompleteDecision("The install command completed.", "Package installed.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("installed");
        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine(),
            runtimeSettings: new NebulaRuntimeSettings
            {
                AutoApproveCommands = true
            });

        var result = await runner.RunAsync(CreateRequest("Install requests."), progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Single(result.Commands);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].AutoApproved);
        Assert.False(result.Commands[0].ApprovedByUser);
        Assert.Equal(CommandSafetyDecisionType.AskApproval, result.Commands[0].SafetyDecision);
        Assert.False(result.Commands[0].IsSafe);
        executorMock.Verify(
            executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_execute_approval_required_command_in_sandbox_when_enabled()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need a package.", "Install package", "pip install requests"),
            CompleteDecision("The sandboxed command completed.", "Package installed.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("executed outside sandbox");

        var sandboxMock = new Mock<ICommandSandbox>();
        sandboxMock.SetupGet(sandbox => sandbox.Mode).Returns(SandboxMode.Docker);
        sandboxMock.Setup(sandbox => sandbox.IsEligible(It.IsAny<ShellKind>())).Returns(true);
        sandboxMock
            .Setup(sandbox => sandbox.RunSandboxedAsync(
                It.IsAny<ShellKind>(),
                It.IsAny<ResolvedCommand>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ShellKind _, ResolvedCommand _, string _, CancellationToken _) =>
                new ShellCommandResult
                {
                    Command = "pip install requests",
                    WorkingDirectory = Environment.CurrentDirectory,
                    StandardOutput = "installed in sandbox",
                    ExitCode = 0
                });

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine(),
            runtimeSettings: new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            commandSandbox: sandboxMock.Object);

        var result = await runner.RunAsync(CreateRequest("Install requests."), progress: null);

Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Single(result.Commands);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].Sandboxed);
        Assert.False(result.Commands[0].AutoApproved);
        Assert.Contains("sandbox", result.Commands[0].Notes, StringComparison.OrdinalIgnoreCase);
        sandboxMock.Verify(
            sandbox => sandbox.RunSandboxedAsync(
                It.IsAny<ShellKind>(),
                It.IsAny<ResolvedCommand>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        executorMock.Verify(
            executor => executor.RunCommandAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_allow_command_on_workspace_allowlist_without_approval()
    {
        using var workspace = new TempTestWorkspace();
        var allowlist = new Mock<ICommandAllowlistService>();
        allowlist
            .Setup(service => service.IsAllowedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var decisions = new Queue<string>(
        [
            ActionDecision("I need a package.", "Install package", "pip install requests"),
            CompleteDecision("The allowlisted command completed.", "Package installed.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("installed");

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine(),
            commandAllowlistService: allowlist.Object);

        var result = await runner.RunAsync(
            CreateRequest("Install requests.", workspaceRoot: workspace.Path),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Single(result.Commands);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].AutoApproved);
        Assert.False(result.Commands[0].ApprovedByUser);
        Assert.Contains(
            "allowlist",
            result.Commands[0].Notes,
            StringComparison.OrdinalIgnoreCase);
        allowlist.Verify(
            service => service.IsAllowedAsync(
                workspace.Path,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        executorMock.Verify(
            executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_request_approval_when_command_is_not_on_workspace_allowlist()
    {
        using var workspace = new TempTestWorkspace();
        var allowlist = new Mock<ICommandAllowlistService>();
        allowlist
            .Setup(service => service.IsAllowedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var decisions = new Queue<string>(
        [
            ActionDecision("I need a package.", "Install package", "pip install requests"),
            CompleteDecision("Package installed.", "Package installed.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine(),
            commandAllowlistService: allowlist.Object);

        var result = await runner.RunAsync(
            CreateRequest("Install requests.", workspaceRoot: workspace.Path),
            progress: null);

        Assert.Equal(ActionExecutionStatus.AwaitingApproval, result.ActionStatus);
        executorMock.Verify(
            executor => executor.RunCommandAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_persist_approved_command_to_workspace_allowlist_for_workspace_scope()
    {
        using var workspace = new TempTestWorkspace();
        var allowlist = new Mock<ICommandAllowlistService>();
        allowlist
            .Setup(service => service.AddAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var llamaClientMock = CreateLlamaClientMock();
        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("installed");

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine(),
            commandAllowlistService: allowlist.Object);

        var request = CreateRequest(
            "Install requests.",
            workspaceRoot: workspace.Path);
        request.ApprovedAction = new AgentApprovedAction
        {
            Objective = "Install package",
            Command = "pip install requests",
            OperationKind = OperationKind.TerminalCommand,
            Scope = ApprovalScope.Workspace
        };

        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Single(result.Commands);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].ApprovedByUser);
        Assert.False(result.Commands[0].AutoApproved);
        Assert.Contains(
            "allowlist",
            result.Commands[0].Notes,
            StringComparison.OrdinalIgnoreCase);
        allowlist.Verify(
            service => service.AddAsync(
                workspace.Path,
                "pip install requests",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_register_category_for_category_scope()
    {
        using var workspace = new TempTestWorkspace();
        var llamaClientMock = CreateLlamaClientMock();
        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("installed");

        var workspaceCategories = new Mock<IWorkspaceCategoryPolicyService>();
        workspaceCategories
            .Setup(service => service.ListAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        workspaceCategories
            .Setup(service => service.AddAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var runtimeSettings = new NebulaRuntimeSettings();
        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine(),
            runtimeSettings: runtimeSettings,
            workspaceCategoryPolicyService: workspaceCategories.Object);

        var request = CreateRequest(
            "Install requests.",
            workspaceRoot: workspace.Path);
        request.ApprovedAction = new AgentApprovedAction
        {
            Objective = "Install package",
            Command = "pip install requests",
            OperationKind = OperationKind.TerminalCommand,
            Scope = ApprovalScope.Category
        };

        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].AutoApproved);
        Assert.Contains(
            "package-install",
            result.Commands[0].Notes,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "neste workspace",
            result.Commands[0].Notes,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            runtimeSettings.AutoApproveCategories,
            category => category.Equals(
                "package-install",
                StringComparison.OrdinalIgnoreCase));
        workspaceCategories.Verify(
            service => service.AddAsync(
                workspace.Path,
                "package-install",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_skip_approval_for_workspace_category()
    {
        using var workspace = new TempTestWorkspace();
        var workspaceCategories = new Mock<IWorkspaceCategoryPolicyService>();
        workspaceCategories
            .Setup(service => service.ListAsync(
                workspace.Path,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new WorkspaceMemoryEntry(
                    Guid.NewGuid(),
                    workspace.Path,
                    WorkspaceMemoryKind.AutoApprovedCategory,
                    "package-install",
                    "package-install",
                    null,
                    DateTimeOffset.UtcNow)
            ]);

        var decisions = new Queue<string>(
        [
            ActionDecision("I need a package.", "Install package", "pip install requests"),
            CompleteDecision("Package installed.", "Package installed.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("installed");

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine(),
            workspaceCategoryPolicyService: workspaceCategories.Object);

        var result = await runner.RunAsync(
            CreateRequest("Install requests.", workspaceRoot: workspace.Path),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Single(result.Commands);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].AutoApproved);
        Assert.False(result.Commands[0].ApprovedByUser);
        Assert.Contains(
            "categoria",
            result.Commands[0].Notes,
            StringComparison.OrdinalIgnoreCase);
        executorMock.Verify(
            executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_skip_approval_for_command_approved_in_conversation()
    {
        using var workspace = new TempTestWorkspace();
        var decisions = new Queue<string>(
        [
            ActionDecision("I need a package.", "Install package", "pip install requests"),
            CompleteDecision("Package installed.", "Package installed.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("installed");

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine());

        var request = CreateRequest(
            "Install requests.",
            workspaceRoot: workspace.Path);
        request.ConversationApprovedCommands =
            ["pip install requests"];

        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Single(result.Commands);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].ApprovedByUser);
        Assert.False(result.Commands[0].AutoApproved);
        Assert.Contains(
            "conversa",
            result.Commands[0].Notes,
            StringComparison.OrdinalIgnoreCase);
        executorMock.Verify(
            executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task run_async_must_request_approval_when_sandbox_is_ineligible_for_the_shell()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need a package.", "Install package", "pip install requests"),
            CompleteDecision("The command was approved and executed.", "Package installed.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();

        var sandboxMock = new Mock<ICommandSandbox>();
        sandboxMock.SetupGet(sandbox => sandbox.Mode).Returns(SandboxMode.Docker);
        sandboxMock.Setup(sandbox => sandbox.IsEligible(It.IsAny<ShellKind>())).Returns(false);

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine(),
            runtimeSettings: new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            commandSandbox: sandboxMock.Object);

var result = await runner.RunAsync(CreateRequest("Install requests."), progress: null);

        Assert.Equal(ActionExecutionStatus.AwaitingApproval, result.ActionStatus);
        sandboxMock.Verify(
            sandbox => sandbox.RunSandboxedAsync(
                It.IsAny<ShellKind>(),
                It.IsAny<ResolvedCommand>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_allow_non_allowlisted_write_in_workspace_when_sandbox_enabled()
    {
        using var workspace = new TempTestWorkspace();
        var targetPath = Path.Combine(workspace.Path, "index.html");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to create the web page inside the workspace.",
                "Create index.html",
                OperationKind.FileWrite,
                content: "<!doctype html><title>Olá</title>",
                targetPath: targetPath,
                workingDirectory: workspace.Path),
            CompleteDecision(
                "The web page was created in the sandbox workspace.",
                "Created index.html.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var sandboxMock = new Mock<ICommandSandbox>();
        sandboxMock.SetupGet(sandbox => sandbox.Mode).Returns(SandboxMode.Docker);

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: CreateExecutorMock(),
            runtimeSettings: new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            commandSandbox: sandboxMock.Object);

        var result = await runner.RunAsync(
            CreateRequest("Crie um arquivo index.html no workspace."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.True(File.Exists(targetPath));
        Assert.Equal(
            ActionExecutionEventKind.Completed,
            result.ActionEvents.Last().Kind);
        Assert.DoesNotContain(
            result.ActionEvents,
            actionEvent => actionEvent.Kind == ActionExecutionEventKind.ApprovalRequired);
    }

    [Fact]
    public async Task run_async_must_request_approval_for_non_allowlisted_write_without_sandbox()
    {
        using var workspace = new TempTestWorkspace();
        var targetPath = Path.Combine(workspace.Path, "index.html");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to create the web page.",
                "Create index.html",
                OperationKind.FileWrite,
                content: "<!doctype html><title>Olá</title>",
                targetPath: targetPath,
                workingDirectory: workspace.Path)
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: CreateExecutorMock());

        var result = await runner.RunAsync(
            CreateRequest("Create an index.html in the workspace."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.AwaitingApproval, result.ActionStatus);
        Assert.False(File.Exists(targetPath));
        Assert.Equal(
            ActionExecutionEventKind.ApprovalRequired,
            result.ActionEvents.Last().Kind);
    }

    [Fact]
    public async Task run_async_must_auto_approve_non_allowlisted_write_when_auto_approval_enabled()
    {
        using var workspace = new TempTestWorkspace();
        var targetPath = Path.Combine(workspace.Path, "index.html");
        const string content = "<!doctype html><title>Olá</title>";

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to create the web page.",
                "Create index.html",
                OperationKind.FileWrite,
                content: content,
                targetPath: targetPath,
                workingDirectory: workspace.Path),
            CompleteDecision(
                "The web page was created.",
                "Created index.html.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: CreateExecutorMock(),
            runtimeSettings: new NebulaRuntimeSettings
            {
                AutoApproveCommands = true
            });

        var result = await runner.RunAsync(
            CreateRequest("Create an index.html in the workspace."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.True(File.Exists(targetPath));
        Assert.Equal(content, await File.ReadAllTextAsync(targetPath));
        Assert.Single(result.Commands);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].AutoApproved);
        Assert.False(result.Commands[0].ApprovedByUser);
        Assert.Equal(CommandSafetyDecisionType.AskApproval, result.Commands[0].SafetyDecision);
    }

    [Fact]
    public async Task run_async_must_execute_explicitly_approved_write_replaying_original_content()
    {
        using var workspace = new TempTestWorkspace();
        var targetPath = Path.Combine(workspace.Path, "index.html");
        const string content = "<!doctype html><title>Aprovado</title>";

        var llamaClientMock = CreateLlamaClientMock();
        SetupAffirmativeVerification(llamaClientMock);
        var runner = CreateRunner(
            llamaClientMock,
            executorMock: CreateExecutorMock());

        var request = CreateRequest("Aprovar e executar: gravar index.html");
        request.MaxSteps = 1;
        request.MaxRetriesPerStep = 0;
        request.ApprovedAction = new AgentApprovedAction
        {
            Objective = "Create index.html",
            Command = $"write-file \"{targetPath}\"",
            OperationKind = OperationKind.FileWrite,
            TargetPath = targetPath,
            Content = content,
            WorkingDirectory = workspace.Path
        };

        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.True(File.Exists(targetPath));
        Assert.Equal(content, await File.ReadAllTextAsync(targetPath));
        Assert.Single(result.Commands);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].ApprovedByUser);
        Assert.Equal(CommandSafetyDecisionType.AskApproval, result.Commands[0].SafetyDecision);
        llamaClientMock.Verify(
            client => client.GetStructuredResponseAsync(
                It.Is<string>(prompt => prompt.Contains("task execution agent", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_allow_planned_patch_with_non_allowlisted_files_in_workspace_when_sandbox_enabled()
    {
        using var workspace = new TempTestWorkspace();

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to create the web project files inside the workspace.",
                "Create the web project",
                OperationKind.PlannedPatch,
                targetPath: workspace.Path,
                plannedFiles:
                [
                    new PlannedPatchFile("index.html", "<!doctype html>"),
                    new PlannedPatchFile("script.js", "console.log('hello');")
                ],
                workingDirectory: workspace.Path),
            CompleteDecision(
                "The web project files were created inside the workspace.",
                "Created the web project.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var sandboxMock = new Mock<ICommandSandbox>();
        sandboxMock.SetupGet(sandbox => sandbox.Mode).Returns(SandboxMode.Docker);

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: CreateExecutorMock(),
            runtimeSettings: new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            commandSandbox: sandboxMock.Object,
            plannedPatchApplier: new PlannedPatchApplier(
                workspaceRoot: workspace.Path,
                controlledTempRoot: workspace.Path));

        var result = await runner.RunAsync(
            CreateRequest("Create a web project with index.html and script.js."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.True(File.Exists(Path.Combine(workspace.Path, "index.html")));
        Assert.True(File.Exists(Path.Combine(workspace.Path, "script.js")));
        Assert.Equal(
            ActionExecutionEventKind.Completed,
            result.ActionEvents.Last().Kind);
        Assert.DoesNotContain(
            result.ActionEvents,
            actionEvent => actionEvent.Kind == ActionExecutionEventKind.ApprovalRequired);
    }

    [Fact]
    public async Task run_async_must_still_block_sensitive_write_even_with_sandbox_enabled()
    {
        using var workspace = new TempTestWorkspace();
        var targetPath = Path.Combine(workspace.Path, ".env");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to create a configuration file.",
                "Create .env",
                OperationKind.FileWrite,
                content: "API_KEY=abc",
                targetPath: targetPath,
                workingDirectory: workspace.Path)
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var sandboxMock = new Mock<ICommandSandbox>();
        sandboxMock.SetupGet(sandbox => sandbox.Mode).Returns(SandboxMode.Docker);

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: CreateExecutorMock(),
            runtimeSettings: new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Docker
            },
            commandSandbox: sandboxMock.Object);

        var result = await runner.RunAsync(
            CreateRequest("Create a .env file."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Unsafe, result.ActionStatus);
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public async Task run_async_must_request_approval_when_sandbox_is_disabled()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need a package.", "Install package", "pip install requests"),
            CompleteDecision("The command was approved and executed.", "Package installed.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();

        var sandboxMock = new Mock<ICommandSandbox>();
        sandboxMock.SetupGet(sandbox => sandbox.Mode).Returns(SandboxMode.Disabled);

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine(),
            runtimeSettings: new NebulaRuntimeSettings
            {
                SandboxMode = SandboxMode.Disabled
            },
            commandSandbox: sandboxMock.Object);

        var result = await runner.RunAsync(CreateRequest("Install requests."), progress: null);

        Assert.Equal(ActionExecutionStatus.AwaitingApproval, result.ActionStatus);
        sandboxMock.Verify(
            sandbox => sandbox.RunSandboxedAsync(
                It.IsAny<ShellKind>(),
                It.IsAny<ResolvedCommand>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_execute_explicitly_approved_command_without_replanning()
    {
        var llamaClientMock = CreateLlamaClientMock();
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "pip install requests",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("installed");
        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateAskApprovalPolicyEngine());

        var request = CreateRequest("Aprovar e executar: pip install requests");
        request.MaxSteps = 1;
        request.MaxRetriesPerStep = 0;
        request.ApprovedAction = new AgentApprovedAction
        {
            Objective = "Install package",
            Command = "pip install requests",
            OperationKind = OperationKind.TerminalCommand
        };

        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Single(result.Commands);
        Assert.True(result.Commands[0].Executed);
        Assert.True(result.Commands[0].ApprovedByUser);
        Assert.False(result.Commands[0].AutoApproved);
        Assert.Equal(CommandSafetyDecisionType.AskApproval, result.Commands[0].SafetyDecision);
        llamaClientMock.Verify(
            client => client.GetStructuredResponseAsync(
                It.Is<string>(prompt => prompt.Contains("task execution agent", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_not_execute_blocked_command_even_when_approved()
    {
        var llamaClientMock = CreateLlamaClientMock();
        SetupAffirmativeVerification(llamaClientMock);
        var executorMock = CreateExecutorMock();
        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateBlockPolicyEngine());

        var request = CreateRequest("Aprovar e executar: rm -rf /");
        request.ApprovedAction = new AgentApprovedAction
        {
            Objective = "Remove everything",
            Command = "rm -rf /",
            OperationKind = OperationKind.TerminalCommand
        };

        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Unsafe, result.ActionStatus);
        Assert.Single(result.Commands);
        Assert.Equal(CommandSafetyDecisionType.Block, result.Commands[0].SafetyDecision);
        executorMock.Verify(
            executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_reject_unsafe_generated_action_without_tool_invocation_or_retry()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision(
                "I need to remove files, but this command requires safety validation.",
                "Remove files",
                "rm -rf /")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            CreateRealPolicyEngine());

        var result = await runner.RunAsync(CreateRequest("Create a marker file."), progress: null);

        Assert.Equal(ActionExecutionStatus.Unsafe, result.ActionStatus);
        Assert.Equal(ActionExecutionEventKind.Unsafe, result.ActionEvents.Last().Kind);
        Assert.DoesNotContain(
            result.ActionEvents,
            actionEvent => actionEvent.Kind == ActionExecutionEventKind.RetryScheduled);
        executorMock.Verify(
            executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task run_async_must_cancel_running_tool_execution_and_emit_cancelled()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need to run the long-running command.", "Run slow command", "slowcmd")
        ]);
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationSource = new CancellationTokenSource();

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("slowcmd", It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                commandStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "never";
            });

        var runner = CreateRunner(llamaClientMock, executorMock);
        var task = runner.RunAsync(
            CreateRequest("Create a script file and run a slow command."),
            progress: null,
            cancellationSource.Token);

        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellationSource.Cancel();

        var result = await task;

        Assert.True(result.IsCancelled);
        Assert.Equal(ActionExecutionStatus.Cancelled, result.ActionStatus);
        Assert.Equal(ActionExecutionEventKind.Cancelled, result.ActionEvents.Last().Kind);
        Assert.DoesNotContain(
            result.ActionEvents,
            actionEvent => actionEvent.Kind == ActionExecutionEventKind.RetryScheduled);
    }

    [Fact]
    public async Task run_async_must_not_accept_completion_when_deterministic_verification_fails()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = Path.Combine(workspace.Path, "Program.cs");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to write the source file.",
                "Write Program.cs",
                OperationKind.FileWrite,
                content: "class Program {}",
                targetPath: sourcePath,
                language: "csharp"),
            CompleteDecision(
                "The source file was written.",
                "Created Program.cs.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var executorMock = CreateExecutorMock();
        executorMock
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, string workingDirectory, CancellationToken _) =>
                FailedResult(
                    command,
                    "error CS1002: ; expected",
                    exitCode: 1,
                    workingDirectory));

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: executorMock,
            deterministicVerificationService: CreateDeterministicVerificationService(
                executorMock,
                new DeterministicStackDetector()));

        var request = CreateRequest("Create a C# program that compiles.");
        request.MaxRetriesPerStep = 0;

        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Contains("verificacao deterministica", result.Response);
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task run_async_must_complete_when_deterministic_verification_passes()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = Path.Combine(workspace.Path, "Program.cs");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to write the source file.",
                "Write Program.cs",
                OperationKind.FileWrite,
                content: "class Program {}",
                targetPath: sourcePath,
                language: "csharp"),
            CompleteDecision(
                "The source file was written.",
                "Created Program.cs.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var executorMock = CreateExecutorMock();
        executorMock
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, string workingDirectory, CancellationToken _) =>
                SuccessResult(command, "Build succeeded. 0 errors"));

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: executorMock,
            deterministicVerificationService: CreateDeterministicVerificationService(
                executorMock,
                new DeterministicStackDetector()));

        var result = await runner.RunAsync(
            CreateRequest("Create a C# program that compiles."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal("Created Program.cs.", result.Response);
        Assert.True(File.Exists(sourcePath));
    }

    private static AgentActionRunRequest CreateRequest(
        string prompt,
        string? workspaceRoot = null)
    {
        return new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = prompt,
            ChatHistoryContext = "[current_user_message]\n" + prompt,
            ModelName = "qwen3:8b",
            WorkspaceRoot = workspaceRoot
        };
    }

    [Fact]
    public async Task run_async_must_scaffold_project_from_template()
    {
        using var workspace = new TempTestWorkspace();
        var projectDirectory = Path.Combine(workspace.Path, "MyProject");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to scaffold the project from the dotnet-console template.",
                "Create a new .NET console project",
                OperationKind.ProjectScaffold,
                templateId: "dotnet-console",
                targetPath: projectDirectory),
            CompleteDecision(
                "The project was scaffolded and verified.",
                "Created the console project from the template.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var runner = CreateRunner(
            llamaClientMock,
            projectTemplateCatalog: new ProjectTemplateCatalog(),
            projectScaffolder: new ProjectScaffolder(
                new ProjectTemplateCatalog(),
                workspaceRoot: workspace.Path,
                controlledTempRoot: workspace.Path),
            projectStackValidator: new ProjectStackValidator(
                new ProjectTemplateCatalog(),
                new WorkspaceMapService(new DeterministicStackDetector())));

        var result = await runner.RunAsync(
            CreateRequest("Create a new .NET console project."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.True(File.Exists(Path.Combine(projectDirectory, "src/App/App.csproj")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, "src/App/Program.cs")));
        Assert.True(File.Exists(Path.Combine(projectDirectory, "README.md")));
        Assert.Contains(
            result.Evidence,
            evidence =>
                evidence.OperationKind == OperationKind.ProjectScaffold &&
                evidence.Success &&
                evidence.Command == "dotnet-console");
        Assert.Contains(
            result.Commands,
            command => command.OperationKind == OperationKind.ProjectScaffold && command.Executed);
        Assert.Contains(
            result.Artifacts,
            artifact => artifact.Name.Contains("App.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task run_async_must_apply_planned_multi_file_patch()
    {
        using var workspace = new TempTestWorkspace();

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to apply the planned patch.",
                "Update the app and its docs",
                OperationKind.PlannedPatch,
                targetPath: workspace.Path,
                plannedFiles:
                [
                    new PlannedPatchFile("src/App.cs", "public class App { }"),
                    new PlannedPatchFile("README.md", "# My Project")
                ]),
            CompleteDecision(
                "The patch was applied.",
                "Applied the planned patch to 2 files.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var runner = CreateRunner(
            llamaClientMock,
            plannedPatchApplier: new PlannedPatchApplier(
                workspaceRoot: workspace.Path,
                controlledTempRoot: workspace.Path));

        var result = await runner.RunAsync(
            CreateRequest("Update the app and its docs"),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal(
            "public class App { }",
            File.ReadAllText(Path.Combine(workspace.Path, "src", "App.cs")));
        Assert.Equal(
            "# My Project",
            File.ReadAllText(Path.Combine(workspace.Path, "README.md")));
        Assert.Contains(
            result.Evidence,
            evidence =>
                evidence.OperationKind == OperationKind.PlannedPatch &&
                evidence.Success);
        Assert.Contains(
            result.Commands,
            command => command.OperationKind == OperationKind.PlannedPatch && command.Executed);
        Assert.Equal(2, result.Artifacts.Count);
    }

    [Fact]
    public async Task run_async_must_request_approval_when_patch_contains_risky_file()
    {
        using var workspace = new TempTestWorkspace();

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to apply the planned patch.",
                "Update the setup script",
                OperationKind.PlannedPatch,
                targetPath: workspace.Path,
                plannedFiles:
                [
                    new PlannedPatchFile("setup.ps1", "Write-Host 'hello'"),
                    new PlannedPatchFile("notes.txt", "hello")
                ])
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var runner = CreateRunner(
            llamaClientMock,
            plannedPatchApplier: new PlannedPatchApplier(
                workspaceRoot: workspace.Path,
                controlledTempRoot: workspace.Path));

        var result = await runner.RunAsync(
            CreateRequest("Update the setup script"),
            progress: null);

        Assert.Equal(ActionExecutionStatus.AwaitingApproval, result.ActionStatus);
        Assert.Equal(ActionExecutionEventKind.ApprovalRequired, result.ActionEvents.Last().Kind);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "setup.ps1")));
        Assert.False(File.Exists(Path.Combine(workspace.Path, "notes.txt")));
        var execution = Assert.Single(result.Commands);
        Assert.Equal(OperationKind.PlannedPatch, execution.OperationKind);
        Assert.Equal(2, execution.PlannedFiles!.Count);
        Assert.Contains("setup.ps1", execution.Notes);
    }

    [Fact]
    public async Task run_async_must_block_patch_with_path_traversal()
    {
        using var workspace = new TempTestWorkspace();

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to apply the planned patch.",
                "Patch outside the project",
                OperationKind.PlannedPatch,
                targetPath: workspace.Path,
                plannedFiles:
                [
                    new PlannedPatchFile("../evil.txt", "x")
                ])
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var runner = CreateRunner(
            llamaClientMock,
            plannedPatchApplier: new PlannedPatchApplier(
                workspaceRoot: workspace.Path,
                controlledTempRoot: workspace.Path));

        var result = await runner.RunAsync(
            CreateRequest("Patch outside the project"),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Unsafe, result.ActionStatus);
        Assert.False(File.Exists(Path.Combine(workspace.Path, "..", "evil.txt")));
    }

    [Fact]
    public async Task run_approved_patch_must_be_applied_when_user_approves()
    {
        using var workspace = new TempTestWorkspace();

        var llamaClientMock = CreateLlamaClientMock();

        var runner = CreateRunner(
            llamaClientMock,
            plannedPatchApplier: new PlannedPatchApplier(
                workspaceRoot: workspace.Path,
                controlledTempRoot: workspace.Path));

        var result = await runner.RunAsync(
            new AgentActionRunRequest
            {
                ConversationId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                Prompt = "Executar patch aprovado",
                ChatHistoryContext = "[approved_patch]",
                ModelName = "qwen3:8b",
                MaxSteps = 1,
                MaxRetriesPerStep = 0,
                ApprovedAction = new AgentApprovedAction
                {
                    Objective = "Update the app",
                    OperationKind = OperationKind.PlannedPatch,
                    TargetPath = workspace.Path,
                    PlannedFiles =
                    [
                        new PlannedPatchFile("App.cs", "public class App { }")
                    ]
                }
            },
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.True(File.Exists(Path.Combine(workspace.Path, "App.cs")));
        Assert.Contains(
            result.Commands,
            command => command.OperationKind == OperationKind.PlannedPatch && command.Executed);
    }

    [Fact]
    public async Task generate_next_step_async_must_include_workspace_and_template_context()
    {
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(
            llamaClientMock,
            new Queue<string>([CompleteDecision("done", "done")]));

        string? capturedPrompt = null;
        llamaClientMock
            .Setup(client => client.GetStructuredResponseAsync(
                It.IsAny<string>(),
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?, CancellationToken>((prompt, _, _) => capturedPrompt = prompt)
            .ReturnsAsync(CompleteDecision("done", "done"));

        var workspaceMapService = new Mock<IWorkspaceMapService>();
        workspaceMapService
            .Setup(service => service.BuildAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkspaceMap(
                @"C:\workspace",
                new WorkspaceStack(
                    WorkspaceStackKind.DotNet,
                    @"C:\workspace\App.csproj",
                    "dotnet build",
                    "dotnet test",
                    null),
                ["App.csproj", "Program.cs", "README.md"],
                [],
                ["AppTests.cs"],
                [new WorkspaceDependency("xunit", "2.9.2", "runtime")],
                ["dotnet build", "dotnet test"]));

        var runner = CreateRunner(
            llamaClientMock,
            projectTemplateCatalog: new ProjectTemplateCatalog(),
            workspaceMapService: workspaceMapService.Object);

        var decision = await runner.GenerateNextStepAsync(
            new AgentActionDecisionRequest
            {
                Objective = "Create a .NET console project"
            });

        Assert.NotNull(decision);
        Assert.Contains("Available project templates", capturedPrompt);
        Assert.Contains("dotnet-console", capturedPrompt);
        Assert.Contains("node-cli", capturedPrompt);
        Assert.Contains("Current workspace context", capturedPrompt);
        Assert.Contains("Detected stack: DotNet", capturedPrompt);
        Assert.Contains("AppTests.cs", capturedPrompt);
        Assert.Contains("xunit", capturedPrompt);
        Assert.Contains("ProjectScaffold", capturedPrompt);
    }

    [Fact]
    public async Task generate_next_step_async_must_use_reference_workspace_from_request()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(
            Path.Combine(workspace.Path, "Program.cs"),
            "Console.WriteLine(\"hi\");");

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(
            llamaClientMock,
            new Queue<string>([CompleteDecision("done", "done")]));

        string? requestedRoot = null;
        var workspaceMapService = new Mock<IWorkspaceMapService>();
        workspaceMapService
            .Setup(service => service.BuildAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((root, _) => requestedRoot = root)
            .ReturnsAsync(new WorkspaceMap(
                workspace.Path,
                new WorkspaceStack(
                    WorkspaceStackKind.Unknown,
                    null,
                    null,
                    null,
                    null),
                [],
                [],
                [],
                [],
                []));

        var runner = CreateRunner(
            llamaClientMock,
            workspaceMapService: workspaceMapService.Object);

        var decision = await runner.GenerateNextStepAsync(
            new AgentActionDecisionRequest
            {
                Objective = "Explain this project",
                WorkspaceRoot = workspace.Path
            });

        Assert.NotNull(decision);
        Assert.Equal(workspace.Path, requestedRoot);
        workspaceMapService.Verify(
            service => service.BuildAsync(
                workspace.Path,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task generate_next_step_async_must_fall_back_to_default_workspace_when_not_specified()
    {
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(
            llamaClientMock,
            new Queue<string>([CompleteDecision("done", "done")]));

        string? requestedRoot = null;
        var workspaceMapService = new Mock<IWorkspaceMapService>();
        workspaceMapService
            .Setup(service => service.BuildAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((root, _) => requestedRoot = root)
            .ReturnsAsync(new WorkspaceMap(
                Environment.CurrentDirectory,
                new WorkspaceStack(
                    WorkspaceStackKind.Unknown,
                    null,
                    null,
                    null,
                    null),
                [],
                [],
                [],
                [],
                []));

        var runner = CreateRunner(
            llamaClientMock,
            workspaceMapService: workspaceMapService.Object);

        var decision = await runner.GenerateNextStepAsync(
            new AgentActionDecisionRequest
            {
                Objective = "Explain this project"
            });

        Assert.NotNull(decision);
        Assert.NotNull(requestedRoot);
        Assert.EndsWith(
            ReferenceWorkspace.DefaultWorkspaceFolderName,
            requestedRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task run_async_must_skip_deterministic_verification_when_disabled()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = Path.Combine(workspace.Path, "Program.cs");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to write the source file.",
                "Write Program.cs",
                OperationKind.FileWrite,
                content: "class Program {}",
                targetPath: sourcePath,
                language: "csharp"),
            CompleteDecision(
                "The source file was written.",
                "Created Program.cs.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var executorMock = CreateExecutorMock();
        executorMock
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, string workingDirectory, CancellationToken _) =>
                FailedResult(
                    command,
                    "error CS1002: ; expected",
                    exitCode: 1,
                    workingDirectory));

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: executorMock,
            deterministicVerificationService: CreateDeterministicVerificationService(
                executorMock,
                new DeterministicStackDetector()),
            runtimeSettings: new NebulaRuntimeSettings
            {
                RequireDeterministicVerification = false
            });

        var result = await runner.RunAsync(
            CreateRequest("Create a C# program that compiles."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal("Created Program.cs.", result.Response);
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task run_async_must_append_working_tree_diff_to_final_report()
    {
        using var workspace = new TempTestWorkspace();
        var sourcePath = Path.Combine(workspace.Path, "note.txt");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to write the note.",
                "Write note.txt",
                OperationKind.FileWrite,
                content: "hello",
                targetPath: sourcePath),
            CompleteDecision(
                "The note was written.",
                "Created note.txt.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var gitDiffMock = new Mock<IGitDiffService>();
        gitDiffMock
            .Setup(service => service.GetWorkingTreeDiffAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitDiffResult(
                true,
                ["note.txt", "external-change.txt"],
                "note.txt | 1 +",
                null));

        var runner = CreateRunner(
            llamaClientMock,
            gitDiffService: gitDiffMock.Object);

        var result = await runner.RunAsync(
            CreateRequest("Create a note file."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Contains("Diff do working tree", result.FinalReport);
        Assert.Contains("external-change.txt", result.FinalReport);
        Assert.Contains("alteracoes fora da acao do agente", result.FinalReport);
    }

    [Fact]
    public async Task file_read_must_work_outside_workspace_when_not_sensitive()
    {
        using var workspace = new TempTestWorkspace();
        var externalRoot = Path.Combine(
            Path.GetTempPath(),
            "nebula-read-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        var targetPath = Path.Combine(externalRoot, "project.txt");
        try
        {
            File.WriteAllText(targetPath, "dotnet 8.0; class Program {}");

            var decisions = new Queue<string>(
            [
                StructuredActionDecision(
                    "I need to read the external file.",
                    "Read project.txt",
                    OperationKind.FileRead,
                    targetPath: targetPath),
                CompleteDecision(
                    "The file was read successfully.",
                    "Read D:/Dev/Backup/project.txt.")
            ]);

            var llamaClientMock = CreateLlamaClientMock();
            SetupDecisionSequence(llamaClientMock, decisions);

            var runner = CreateRunner(llamaClientMock);
            var result = await runner.RunAsync(
                CreateRequest("Read D:/Dev/Backup/project.txt."),
                progress: null);

            Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
            Assert.Equal("Read D:/Dev/Backup/project.txt.", result.Response);
            Assert.True(result.Commands.Single().Executed);
            Assert.Contains("dotnet 8.0", result.Commands.Single().Output);
        }
        finally
        {
            Directory.Delete(externalRoot, recursive: true);
        }
    }

    [Fact]
    public async Task file_read_under_operating_system_root_must_request_approval()
    {
        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to read the system file.",
                "Read win.ini",
                OperationKind.FileRead,
                targetPath: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "win.ini"))
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var runner = CreateRunner(llamaClientMock);
        var result = await runner.RunAsync(
            CreateRequest("Read C:/Windows/win.ini."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.AwaitingApproval, result.ActionStatus);
    }

    [Fact]
    public async Task file_read_of_sensitive_material_must_be_blocked_even_outside_workspace()
    {
        using var workspace = new TempTestWorkspace();
        var targetPath = Path.Combine(
            Path.GetTempPath(),
            "nebula-secret-test-" + Guid.NewGuid().ToString("N"),
            "backup.env");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        try
        {
            File.WriteAllText(targetPath, "API_KEY=secret");

            var decisions = new Queue<string>(
            [
                StructuredActionDecision(
                    "I need to read the env file.",
                    "Read backup.env",
                    OperationKind.FileRead,
                    targetPath: targetPath)
            ]);

            var llamaClientMock = CreateLlamaClientMock();
            SetupDecisionSequence(llamaClientMock, decisions);

            var runner = CreateRunner(llamaClientMock);
            var result = await runner.RunAsync(
                CreateRequest("Read the .env backup."),
                progress: null);

            Assert.Equal(ActionExecutionStatus.Unsafe, result.ActionStatus);
            Assert.Contains(
                "interrompida",
                result.Response,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(targetPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task run_async_must_repair_within_verification_retry_limit()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = Path.Combine(workspace.Path, "Program.cs");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to write the source file.",
                "Write Program.cs",
                OperationKind.FileWrite,
                content: "class Program { broken",
                targetPath: sourcePath,
                language: "csharp"),
            CompleteDecision(
                "The source file was written.",
                "Created Program.cs."),
            StructuredActionDecision(
                "I need to fix the source file.",
                "Fix Program.cs",
                OperationKind.FileWrite,
                content: "class Program {}",
                targetPath: sourcePath,
                language: "csharp"),
            CompleteDecision(
                "The source file was fixed.",
                "Fixed Program.cs.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var executorMock = CreateExecutorMock();
        var verificationCalls = 0;
        executorMock
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, string workingDirectory, CancellationToken _) =>
            {
                if (command.Contains("dotnet format", StringComparison.OrdinalIgnoreCase))
                {
                    return SuccessResult(command, "Formatting complete.");
                }

                verificationCalls++;
                return verificationCalls == 1
                    ? FailedResult(
                        command,
                        "error CS1002: ; expected",
                        exitCode: 1,
                        workingDirectory)
                    : SuccessResult(command, "Build succeeded. 0 errors");
            });

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: executorMock,
            deterministicVerificationService: CreateDeterministicVerificationService(
                executorMock,
                new DeterministicStackDetector()));

        var result = await runner.RunAsync(
            CreateRequest("Create a C# program that compiles."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal("Fixed Program.cs.", result.Response);
        Assert.Equal(2, verificationCalls);
    }

    [Fact]
    public async Task run_async_must_fail_when_verification_retry_limit_is_exceeded()
    {
        using var workspace = new TempTestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = Path.Combine(workspace.Path, "Program.cs");

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to write the source file.",
                "Write Program.cs",
                OperationKind.FileWrite,
                content: "class Program { broken",
                targetPath: sourcePath,
                language: "csharp"),
            CompleteDecision(
                "The source file was written.",
                "Created Program.cs."),
            StructuredActionDecision(
                "I need to fix the source file.",
                "Fix Program.cs",
                OperationKind.FileWrite,
                content: "class Program { still broken",
                targetPath: sourcePath,
                language: "csharp"),
            CompleteDecision(
                "The source file was fixed.",
                "Fixed Program.cs.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var executorMock = CreateExecutorMock();
        var verificationCalls = 0;
        executorMock
            .As<IDetailedShellExecutor>()
            .Setup(detail => detail.RunCommandDetailedAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, string workingDirectory, CancellationToken _) =>
            {
                verificationCalls++;
                return FailedResult(
                    command,
                    "error CS1002: ; expected",
                    exitCode: 1,
                    workingDirectory);
            });

        var runner = CreateRunner(
            llamaClientMock,
            executorMock: executorMock,
            deterministicVerificationService: CreateDeterministicVerificationService(
                executorMock,
                new DeterministicStackDetector()),
            runtimeSettings: new NebulaRuntimeSettings
            {
                MaxVerificationRetries = 1
            });

        var result = await runner.RunAsync(
            CreateRequest("Create a C# program that compiles."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Contains(
            "Limite de correcoes apos falha de verificacao",
            result.Response,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, verificationCalls);
    }

    [Fact]
    public async Task file_write_must_request_approval_when_target_was_modified_after_run_started()
    {
        using var workspace = new TempTestWorkspace();
        var targetPath = Path.Combine(workspace.Path, "existing.txt");
        File.WriteAllText(targetPath, "original");
        File.SetLastWriteTimeUtc(targetPath, DateTime.UtcNow.AddMinutes(5));

        var decisions = new Queue<string>(
        [
            StructuredActionDecision(
                "I need to update the file.",
                "Update existing.txt",
                OperationKind.FileWrite,
                content: "new content",
                targetPath: targetPath)
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var operationPolicyMock = new Mock<IOperationPolicyEngine>();
        operationPolicyMock
            .Setup(engine => engine.EvaluateAsync(
                It.IsAny<OperationPolicyRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationPolicyRequest request, CancellationToken _) =>
                request.Classification?.Source == "ConcurrentModificationGuard"
                    ? new CommandSafetyDecision(
                        CommandSafetyDecisionType.AskApproval,
                        CommandIntent.NeedsApproval,
                        0.99,
                        ["Concurrent modification detected."])
                    : new CommandSafetyDecision(
                        CommandSafetyDecisionType.Allow,
                        CommandIntent.SafeWriteLocal,
                        1,
                        ["Allowed by test policy."]));

        var runner = CreateRunner(
            llamaClientMock,
            operationPolicyEngine: operationPolicyMock.Object);

        var result = await runner.RunAsync(
            CreateRequest("Update existing.txt."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.AwaitingApproval, result.ActionStatus);
        Assert.Equal("original", File.ReadAllText(targetPath));
    }

    [Fact]
    public async Task terminal_command_must_timeout_according_to_settings()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision("I need to run the slow command.", "Run slow command", "slowcmd")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "slowcmd",
                It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken ct) =>
                Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith(
                    _ => string.Empty,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default));

        var runner = CreateRunner(
            llamaClientMock,
            executorMock,
            runtimeSettings: new NebulaRuntimeSettings
            {
                CommandTimeoutSeconds = 1
            });

        var request = CreateRequest("Run the slow command.");
        request.MaxRetriesPerStep = 0;

        var result = await runner.RunAsync(request, progress: null);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Contains(
            result.Commands,
            command => command.StandardError.Contains("timeout", StringComparison.OrdinalIgnoreCase));
    }

    private static AgentActionRunner CreateRunner(
        Mock<ILlamaClient> llamaClientMock,
        Mock<IShellExecutor>? executorMock = null,
        ICommandPolicyEngine? commandPolicyEngine = null,
        Mock<ILogger>? loggerMock = null,
        IKnowledgeQueryService? knowledgeQueryService = null,
        ILearningEngine? learningEngine = null,
        NebulaRuntimeSettings? runtimeSettings = null,
        IAgentRunStore? agentRunStore = null,
        IDeterministicVerificationService? deterministicVerificationService = null,
        IProjectTemplateCatalog? projectTemplateCatalog = null,
        IProjectScaffolder? projectScaffolder = null,
        IProjectStackValidator? projectStackValidator = null,
        IWorkspaceMapService? workspaceMapService = null,
        IPlannedPatchApplier? plannedPatchApplier = null,
        IOperationPolicyEngine? operationPolicyEngine = null,
        IGitDiffService? gitDiffService = null,
        ICommandSandbox? commandSandbox = null,
        ICommandAllowlistService? commandAllowlistService = null,
        IWorkspaceCategoryPolicyService? workspaceCategoryPolicyService = null)
    {
        return new AgentActionRunner(
            llamaClientMock.Object,
            (executorMock ?? CreateExecutorMock()).Object,
            CreateJsonExtractorMock().Object,
            (loggerMock ?? CreateLoggerMock()).Object,
            commandPolicyEngine: commandPolicyEngine ?? CreateAllowPolicyEngine(),
            learningEngine: learningEngine,
            knowledgeQueryService: knowledgeQueryService,
            runtimeSettings: runtimeSettings,
            agentRunStore: agentRunStore,
            deterministicVerificationService: deterministicVerificationService,
            projectTemplateCatalog: projectTemplateCatalog,
            projectScaffolder: projectScaffolder,
            projectStackValidator: projectStackValidator,
            workspaceMapService: workspaceMapService,
            plannedPatchApplier: plannedPatchApplier,
            operationPolicyEngine: operationPolicyEngine,
            gitDiffService: gitDiffService,
            commandSandbox: commandSandbox,
            commandAllowlistService: commandAllowlistService,
            workspaceCategoryPolicyService: workspaceCategoryPolicyService);
    }

    private static IDeterministicVerificationService CreateDeterministicVerificationService(
        Mock<IShellExecutor> executorMock,
        IWorkspaceStackDetector detector)
    {
        return new Nebula.Agent.Application.DeterministicVerificationService(
            detector,
            executorMock.Object,
            CreateLoggerMock().Object);
    }

    private static ICommandPolicyEngine CreateAllowPolicyEngine()
    {
        var mock = new Mock<ICommandPolicyEngine>();
        mock.Setup(engine => engine.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => new CommandSafetyDecision(
                CommandSafetyDecisionType.Allow,
                CommandIntent.SafeExecuteLocal,
                1,
                ["Allowed by test policy."]));
        return mock.Object;
    }

    private static ICommandPolicyEngine CreateAskApprovalPolicyEngine()
    {
        var mock = new Mock<ICommandPolicyEngine>();
        mock.Setup(engine => engine.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => new CommandSafetyDecision(
                CommandSafetyDecisionType.AskApproval,
                CommandIntent.PackageInstall,
                0.99,
                ["Requires approval by test policy."]));
        return mock.Object;
    }

    private static ICommandPolicyEngine CreateBlockPolicyEngine()
    {
        var mock = new Mock<ICommandPolicyEngine>();
        mock.Setup(engine => engine.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => new CommandSafetyDecision(
                CommandSafetyDecisionType.Block,
                CommandIntent.Blocked,
                1,
                ["Blocked by test policy."]));
        return mock.Object;
    }

    private static ICommandPolicyEngine CreateRealPolicyEngine()
    {
        var deterministic = new Nebula.Services.Safety.DeterministicCommandClassifier();
        var unavailableMl = new Nebula.Services.Safety.MlNetCommandClassifier(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip"));
        return new Nebula.Services.Safety.CommandPolicyEngine(
            new Nebula.Services.Safety.CompositeCommandClassifier(deterministic, unavailableMl));
    }

    private static Mock<ILlamaClient> CreateLlamaClientMock()
    {
        var mock = new Mock<ILlamaClient>();
        mock.SetupGet(client => client.SelectedModel).Returns("qwen3:8b");
        return mock;
    }

    private static Mock<IShellExecutor> CreateExecutorMock()
    {
        var mock = new Mock<IShellExecutor>();
        mock
            .Setup(executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        return mock;
    }

    private static Mock<IShellExecutor> CreateDetailedExecutorMock()
    {
        var mock = new Mock<IShellExecutor>();
        mock.As<IDetailedShellExecutor>();
        return mock;
    }

    private static Mock<IJsonExtractor> CreateJsonExtractorMock()
    {
        var mock = new Mock<IJsonExtractor>();
        mock
            .Setup(extractor => extractor.ExtractJsonObject(It.IsAny<string>()))
            .Returns((string input) => input);
        return mock;
    }

    private static Mock<ILogger> CreateLoggerMock() => new();

    private static void SetupDecisionSequence(
        Mock<ILlamaClient> llamaClientMock,
        Queue<string> decisions)
    {
        llamaClientMock
            .Setup(client => client.GetStructuredResponseAsync(
                It.Is<string>(prompt => prompt.Contains("task execution agent", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<object?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => decisions.Dequeue());
    }

    private static void SetupAffirmativeVerification(Mock<ILlamaClient> llamaClientMock)
    {
        llamaClientMock
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("Response only", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Yes");
    }

    private static string ActionDecision(
        string reasoningSummary,
        string objective,
        string command)
    {
        return $$"""
            {
              "reasoningSummary": "{{reasoningSummary}}",
              "isComplete": false,
              "completionMessage": "",
              "action": {
                "objective": "{{objective}}",
                "command": "{{command}}",
                "requiresSafetyReview": true
              }
            }
            """;
    }

    private static string CompleteDecision(
        string reasoningSummary,
        string completionMessage)
    {
        return $$"""
            {
              "reasoningSummary": "{{reasoningSummary}}",
              "isComplete": true,
              "completionMessage": "{{completionMessage}}",
              "action": null
            }
            """;
    }

    private static string StructuredActionDecision(
        string reasoningSummary,
        string objective,
        OperationKind operationKind,
        string command = "",
        string? content = null,
        string? targetPath = null,
        string? language = null,
        string? templateId = null,
        IReadOnlyList<PlannedPatchFile>? plannedFiles = null,
        string? workingDirectory = null)
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
                TemplateId = templateId,
                PlannedFiles = plannedFiles,
                Language = language,
                WorkingDirectory = workingDirectory,
                RequiresSafetyReview = true
            }
        });
    }

    private static string ErrorReflectionDecision(
        string hypothesis,
        string alternativeAction,
        string nextCommand)
    {
        return $$"""
            {
              "hypothesis": "{{hypothesis}}",
              "alternativeAction": "{{alternativeAction}}",
              "nextCommand": "{{nextCommand}}"
            }
            """;
    }

    private static ShellCommandResult FailedResult(
        string command,
        string standardError,
        int exitCode,
        string? workingDirectory = null)
    {
        return new ShellCommandResult
        {
            Command = command,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            StandardError = standardError,
            ExitCode = exitCode,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private static ShellCommandResult SuccessResult(
        string command,
        string standardOutput)
    {
        return new ShellCommandResult
        {
            Command = command,
            WorkingDirectory = Environment.CurrentDirectory,
            StandardOutput = standardOutput,
            ExitCode = 0,
            Timestamp = DateTimeOffset.UtcNow
        };
    }
}

internal sealed class TempTestWorkspace : IDisposable
{
    public TempTestWorkspace()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Nebula",
            "tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // best effort cleanup
        }
    }
}
