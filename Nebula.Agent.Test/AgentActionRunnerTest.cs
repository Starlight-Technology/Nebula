using Moq;
using Nebula.Agent.Data;
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
    }

    [Fact]
    public async Task run_async_must_complete_simple_react_objective_over_multiple_iterations()
    {
        var decisions = new Queue<string>(
        [
            ActionDecision(
                "I need to create the script before it can be executed.",
                "Create hello.py",
                "echo print('hello world') > hello.py"),
            ActionDecision(
                "The script exists, so I need to run it and inspect the output.",
                "Run hello.py",
                "python hello.py"),
            CompleteDecision(
                "The observed output matches the requested result.",
                "Created and ran hello.py successfully.")
        ]);

        var llamaClientMock = CreateLlamaClientMock();
        SetupDecisionSequence(llamaClientMock, decisions);
        SetupAffirmativeVerification(llamaClientMock);

        var executorMock = CreateExecutorMock();
        executorMock
            .Setup(executor => executor.RunCommandAsync(
                "echo print('hello world') > hello.py",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("file created");
        executorMock
            .Setup(executor => executor.RunCommandAsync("python hello.py", It.IsAny<CancellationToken>()))
            .ReturnsAsync("hello world");

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(
            CreateRequest("Create a Python script that prints hello world and run it."),
            progress: null);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal("Created and ran hello.py successfully.", result.Response);
        Assert.Equal(2, result.Commands.Count);
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
            executor => executor.RunCommandAsync("python hello.py", It.IsAny<CancellationToken>()),
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
            .Setup(executor => executor.RunCommandAsync("dir", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Directory listing");

        var runner = CreateRunner(llamaClientMock, executorMock);
        var result = await runner.RunAsync(CreateRequest("List files in the current directory."), progress: null);

        Assert.Contains(
            result.ActionEvents,
            actionEvent =>
                actionEvent.Kind == ActionExecutionEventKind.ActionStarted &&
                actionEvent.Command == "dir");
        Assert.Contains(
            result.ActionEvents,
            actionEvent =>
                actionEvent.Kind == ActionExecutionEventKind.Observation &&
                actionEvent.ToolResponse == "Directory listing");
        executorMock.Verify(
            executor => executor.RunCommandAsync("dir", It.IsAny<CancellationToken>()),
            Times.Once);
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
        Assert.Equal(ActionExecutionEventKind.Failed, result.ActionEvents.Last().Kind);
        executorMock.Verify(
            executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
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
        var runner = CreateRunner(llamaClientMock, executorMock);

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
        Mock<IShellExecutor>? executorMock = null)
    {
        return new AgentActionRunner(
            llamaClientMock.Object,
            (executorMock ?? CreateExecutorMock()).Object,
            CreateJsonExtractorMock().Object,
            CreateLoggerMock().Object);
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
}
