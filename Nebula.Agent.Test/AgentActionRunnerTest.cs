using System.Text.Json;

using Moq;
using Nebula.Agent.Data;
using Nebula.Core.Learning;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Runner;

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
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IProgress<LlamaStreamUpdate>?, CancellationToken>(
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
        Assert.Contains("StepNumber: 2", capturedPrompt);
        Assert.Contains("RetryNumber: 1", capturedPrompt);
        Assert.Contains("Never reveal chain-of-thought", capturedPrompt);
        Assert.Contains(
            "Você é um agente executor. Você deve observar o resultado real de cada comando",
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
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IProgress<LlamaStreamUpdate>?, CancellationToken>(
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
                "Executed successfully.")
        ]);
        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);

        var result = await CreateRunner(llamaClientMock).RunAsync(
            CreateRequest("Execute something."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Empty(result.Evidence);
        Assert.Contains(
            "Nao ha evidencia suficiente",
            result.Response,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task run_async_must_route_learning_request_without_inventing_sources()
    {
        var llamaClientMock = CreateLlamaClientMock();

        var result = await CreateRunner(llamaClientMock).RunAsync(
            CreateRequest("aprenda Python basico"),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Equal(
            "Pesquisa web não configurada. Configure WebResearch:Provider e WebResearch:ApiKey.",
            result.Response);
        llamaClientMock.Verify(
            client => client.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
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
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IProgress<LlamaStreamUpdate>?, CancellationToken>(
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
        Assert.Contains("RetryNumber: 1", capturedPrompts[1]);
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
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IProgress<LlamaStreamUpdate>?, CancellationToken>(
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
        Assert.Contains("Recent execution history", decisionPrompts[^1]);
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

    private static AgentActionRunRequest CreateRequest(string prompt)
    {
        return new AgentActionRunRequest
        {
            ConversationId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            Prompt = prompt,
            ChatHistoryContext = "[current_user_message]\n" + prompt,
            ModelName = "qwen3:8b"
        };
    }

    private static AgentActionRunner CreateRunner(
        Mock<ILlamaClient> llamaClientMock,
        Mock<IShellExecutor>? executorMock = null,
        ICommandPolicyEngine? commandPolicyEngine = null,
        Mock<ILogger>? loggerMock = null,
        IKnowledgeQueryService? knowledgeQueryService = null)
    {
        return new AgentActionRunner(
            llamaClientMock.Object,
            (executorMock ?? CreateExecutorMock()).Object,
            CreateJsonExtractorMock().Object,
            (loggerMock ?? CreateLoggerMock()).Object,
            commandPolicyEngine: commandPolicyEngine ?? CreateAllowPolicyEngine(),
            knowledgeQueryService: knowledgeQueryService);
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
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
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
