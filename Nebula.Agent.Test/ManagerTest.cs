using System.Diagnostics;
using Moq;
using Nebula.Agent;
using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Core.Agent;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public class ManagerTest
{
    [Fact]
    public async Task manage_response_must_return_empty_prompt_message_when_prompt_is_empty()
    {
        var llamaClientMock = create_llama_client_mock();
        var manager = create_manager(llamaClientMock);

        var result = await manager.ManageResponse(ChatMessage(string.Empty));

        Assert.Equal("The prompt are empty, write something.", result);
    }

    [Fact]
    public async Task manage_response_must_return_response_when_chat()
    {
        const string prompt = "Hello, how are you?";
        const string response = "I'm doing well, thanks for asking!";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(It.Is<string>(text =>
                text.Contains("CHAT MODE", StringComparison.Ordinal) &&
                text.Contains(prompt, StringComparison.Ordinal))))
            .ReturnsAsync(response);

        var manager = create_manager(llamaClientMock);

        var result = await manager.ManageResponse(ChatMessage(prompt));

        Assert.Equal(response, result);
        llamaClientMock.Verify(client => client.GetResponseAsync(It.Is<string>(text =>
            text.Contains("CHAT MODE", StringComparison.Ordinal) &&
            text.Contains(prompt, StringComparison.Ordinal))), Times.Once);
    }

    [Fact]
    public async Task manage_response_must_return_response_when_action()
    {
        const string prompt = "list files on c:";
        var llamaClientMock = create_llama_client_mock();
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        AgentActionRunRequest? captured = null;
        orchestratorMock
            .Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActionRunRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Guid.NewGuid());

        var manager = create_manager(
            llamaClientMock,
            orchestrator: orchestratorMock.Object);

        var result = await manager.ManageResponse(AgentMessage(prompt));

        Assert.Equal("Agent run started in background...", result);
        Assert.NotNull(captured);
        Assert.Equal(prompt, captured!.Prompt);
        Assert.Equal(InteractionMode.Agent, captured.Mode);
        orchestratorMock.Verify(o => o.RunAsync(It.Is<AgentActionRunRequest>(r => r.Prompt == prompt), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task manage_response_must_save_prompt_and_response_when_chat()
    {
        const string prompt = "What is Nebula?";
        const string response = "Nebula is a terminal agent.";

        PromptRequest? savedRequest = null;
        Guid updatedRequestId = Guid.Empty;
        string? updatedResponse = null;

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(It.Is<string>(text =>
                text.Contains("CHAT MODE", StringComparison.Ordinal) &&
                text.Contains(prompt, StringComparison.Ordinal))))
            .ReturnsAsync(response);

        var promptRepositoryMock = create_prompt_repository_mock();
        promptRepositoryMock
            .Setup(repository => repository.SaveAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PromptRequest, CancellationToken>((request, _) => savedRequest = clone_prompt_request(request))
            .ReturnsAsync((PromptRequest request, CancellationToken _) => request);
        promptRepositoryMock
            .Setup(repository => repository.UpdateResponseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((id, savedResponse, _) =>
            {
                updatedRequestId = id;
                updatedResponse = savedResponse;
            })
            .ReturnsAsync((Guid id, string savedResponse, CancellationToken _) => new PromptRequest
            {
                Id = id,
                Response = savedResponse
            });

        var manager = create_manager(llamaClientMock, promptRepositoryMock: promptRepositoryMock);

        var result = await manager.ManageResponse(ChatMessage(prompt));

        Assert.Equal(response, result);
        Assert.NotNull(savedRequest);
        Assert.Equal(prompt, savedRequest!.Prompt);
        Assert.Equal(InteractionMode.Chat.ToString(), savedRequest.Classification);
        Assert.Equal(savedRequest.Id, updatedRequestId);
        Assert.Equal(response, updatedResponse);
    }

    [Fact]
    public async Task manage_response_must_save_prompt_and_response_when_action()
    {
        const string prompt = "list files on c:";
        PromptRequest? savedRequest = null;
        var llamaClientMock = create_llama_client_mock();
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        AgentActionRunRequest? captured = null;
        orchestratorMock
            .Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActionRunRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Guid.NewGuid());
        var promptRepositoryMock = create_prompt_repository_mock();
        promptRepositoryMock
            .Setup(repository => repository.SaveAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PromptRequest, CancellationToken>((request, _) => savedRequest = clone_prompt_request(request))
            .ReturnsAsync((PromptRequest request, CancellationToken _) => request);
        var manager = create_manager(
            llamaClientMock,
            promptRepositoryMock: promptRepositoryMock,
            orchestrator: orchestratorMock.Object);
        var result = await manager.ManageResponse(AgentMessage(prompt));
        Assert.Equal("Agent run started in background...", result);
        Assert.NotNull(savedRequest);
        Assert.NotNull(captured);
        Assert.Equal(prompt, captured!.Prompt);
        Assert.Equal(InteractionMode.Agent, captured.Mode);
        orchestratorMock.Verify(o => o.RunAsync(It.Is<AgentActionRunRequest>(r => r.Prompt == prompt), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task manage_conversation_async_must_block_unsafe_action_before_planning()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage("Delete system files"));
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Contains("started in background", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task manage_conversation_async_must_extract_reasoning_when_model_returns_think_block()
    {
        const string prompt = "Explain Nebula";
        const string rawResponse = "<think>Analyzing the request</think>Nebula is a local AI assistant.";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(It.Is<string>(text =>
                text.Contains("CHAT MODE", StringComparison.Ordinal) &&
                text.Contains(prompt, StringComparison.Ordinal))))
            .ReturnsAsync(rawResponse);

        var manager = create_manager(llamaClientMock);

        var result = await manager.ManageConversationAsync(ChatMessage(prompt));

        Assert.Equal("Nebula is a local AI assistant.", result.Response);
        Assert.Equal("Analyzing the request", result.Reasoning);
        Assert.Equal(InteractionMode.Chat.ToString(), result.Classification);
    }

    [Fact]
    public async Task manage_conversation_async_must_report_streaming_updates_for_chat()
    {
        const string prompt = "Explain the flow";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(
                It.Is<string>(text =>
                    text.Contains("CHAT MODE", StringComparison.Ordinal) &&
                    text.Contains(prompt, StringComparison.Ordinal)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, IProgress<LlamaStreamUpdate>?, CancellationToken>((_, progress, _) =>
            {
                progress?.Report(new LlamaStreamUpdate
                {
                    Reasoning = "Partial reasoning"
                });
                progress?.Report(new LlamaStreamUpdate
                {
                    Reasoning = "Partial reasoning",
                    Response = "Partial response"
                });

                return Task.FromResult("<think>Partial reasoning</think>Final response");
            });

        var manager = create_manager(llamaClientMock);
        ConversationTurn? partialUpdate = null;
        var progress = new InlineProgress<ConversationTurn>(update => partialUpdate = update);

        var result = await manager.ManageConversationAsync(ChatMessage(prompt), progress, CancellationToken.None);

        Assert.NotNull(partialUpdate);
        Assert.Equal("Partial response", partialUpdate!.Response);
        Assert.Equal("Partial reasoning", partialUpdate.Reasoning);
        Assert.Equal("Final response", result.Response);
        Assert.Equal("Partial reasoning", result.Reasoning);
    }

    [Fact]
    public async Task manage_conversation_async_must_report_failure_when_action_plan_is_invalid()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage("Create a script that says hi."));
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Contains("started in background", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task manage_conversation_async_must_route_agent_mode_without_reclassifying_content()
    {
        const string prompt = "Diga oi em uma linha.";
        var llamaClientMock = create_llama_client_mock();
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        AgentActionRunRequest? captured = null;
        orchestratorMock
            .Setup(o => o.RunAsync(
                It.Is<AgentActionRunRequest>(request =>
                    request.Prompt == prompt &&
                    request.Mode == InteractionMode.Agent),
                It.IsAny<CancellationToken>()))
            .Callback<AgentActionRunRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Guid.NewGuid());

        var manager = create_manager(llamaClientMock, orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage(prompt));

        Assert.Equal(InteractionMode.Agent, result.Mode);
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Equal("Agent run started in background...", result.Response);
        Assert.NotNull(captured);
        Assert.Equal(prompt, captured!.Prompt);
        orchestratorMock.VerifyAll();
        llamaClientMock.Verify(
            client => client.GetResponseAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task manage_conversation_async_must_route_chat_mode_without_calling_agent_runner()
    {
        const string prompt = "Crie um arquivo teste.txt";
        const string response = "Voce pode criar o arquivo com um editor de texto.";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(It.Is<string>(text =>
                text.Contains("CHAT MODE", StringComparison.Ordinal) &&
                text.Contains(prompt, StringComparison.Ordinal))))
            .ReturnsAsync(response);

        var loggerMock = create_logger_mock();
        var executorMock = create_executor_mock();
        var actionRunnerMock = new Mock<IAgentActionRunner>();
        var manager = create_manager(
            llamaClientMock,
            executorMock,
            loggerMock: loggerMock,
            actionRunner: actionRunnerMock.Object);

        var result = await manager.ManageConversationAsync(ChatMessage(prompt));

        Assert.Equal(InteractionMode.Chat, result.Mode);
        Assert.Equal(response, result.Response);
        actionRunnerMock.Verify(runner => runner.RunAsync(
            It.IsAny<AgentActionRunRequest>(),
            It.IsAny<IProgress<ConversationTurn>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        executorMock.Verify(
            executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        loggerMock.Verify(
            logger => logger.Log(It.Is<string>(message => message.Contains("[CHAT]", StringComparison.Ordinal))),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task chat_mode_must_explain_dir_without_executing_it()
    {
        const string prompt = "Como funciona o comando dir?";
        const string response = "O comando dir lista arquivos e pastas no Windows.";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(It.Is<string>(text =>
                text.Contains("CHAT MODE", StringComparison.Ordinal) &&
                text.Contains(prompt, StringComparison.Ordinal))))
            .ReturnsAsync(response);
        var executorMock = create_executor_mock();
        var manager = create_manager(llamaClientMock, executorMock);

        var result = await manager.ManageConversationAsync(ChatMessage(prompt));

        Assert.Equal(InteractionMode.Chat, result.Mode);
        Assert.Equal(response, result.Response);
        Assert.Empty(result.Commands);
        executorMock.Verify(
            executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(
        "Execute dir na unidade D",
        "dir D:",
        "Get-ChildItem",
        "Directory listing")]
    [InlineData(
        "Crie um arquivo teste.txt",
        "New-Item -ItemType File -Path 'teste.txt'",
        "New-Item -ItemType File",
        "Arquivo criado")]
    public async Task agent_mode_must_execute_and_report_observed_evidence(
        string prompt,
        string proposedCommand,
        string? resolvedCommandFragment,
        string toolOutput)
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage(prompt));
        Assert.Equal(InteractionMode.Agent, result.Mode);
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Contains("started in background", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task agent_mode_must_report_real_command_failure_without_claiming_success()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage("missing-command"));
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Contains("started in background", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task manage_conversation_async_must_reuse_conversation_id_and_send_history_to_model()
    {
        var capturedPrompts = new List<string>();
        var memoryRepository = new InMemoryConversationMemoryRepository();

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(It.IsAny<string>()))
            .Callback<string>(capturedPrompts.Add)
            .ReturnsAsync("Resposta do mock");

        var manager = create_manager(llamaClientMock, conversationMemoryRepository: memoryRepository);

        var firstTurn = await manager.ManageConversationAsync(ChatMessage("Explique Nebula"));
        var secondTurn = await manager.ManageConversationAsync(ChatMessage("Agora explique ela em uma linha"));

        Assert.Equal(firstTurn.ConversationId, secondTurn.ConversationId);
        Assert.Equal(manager.ActiveConversationId, secondTurn.ConversationId);
        Assert.Equal(2, capturedPrompts.Count);
        Assert.Contains("[conversation_state]", capturedPrompts[1]);
        Assert.Contains("user: Explique Nebula", capturedPrompts[1]);
        Assert.Contains("assistant: Resposta do mock", capturedPrompts[1]);
        Assert.Contains("[current_user_message]", capturedPrompts[1]);
        Assert.Contains("Agora explique ela em uma linha", capturedPrompts[1]);

        var persistedMessages = await memoryRepository.GetRecentMessagesAsync(secondTurn.ConversationId, 10);
        var persistedState = await memoryRepository.GetStateAsync(secondTurn.ConversationId);

        Assert.Equal(4, persistedMessages.Count);
        Assert.NotNull(persistedState);
        Assert.Contains("Explique Nebula", persistedState!.Summary);
    }

    [Fact]
    public async Task manage_conversation_async_must_send_chat_history_to_action_planner()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        AgentActionRunRequest? captured = null;
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActionRunRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        await manager.ManageConversationAsync(AgentMessage("first"));
        await manager.ManageConversationAsync(AgentMessage("second"));
        var result = await manager.ManageConversationAsync(AgentMessage("third"));
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task resume_task_async_must_build_context_from_plan_and_last_step()
    {
        var runId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var lastStep = new AgentStepRecord(
            Guid.NewGuid(),
            runId,
            2,
            1,
            OperationKind.TerminalCommand,
            "Run the tests",
            "dotnet test",
            "C:\\work",
            null,
            1,
            false,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            "output",
            "error: tests failed");
        var run = new AgentRun(
            runId,
            conversationId,
            Guid.NewGuid(),
            "Run the project tests.",
            "qwen3:8b",
            "Failed",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            null,
            null,
            false,
            [lastStep],
            "1. Build the project - completed.\n2. Run the tests - Failed.",
            [],
            [],
            @"C:\projects\nebula-demo");
        AgentRun? capturedRun = null;
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock
            .Setup(o => o.ResumeAsync(It.IsAny<AgentRun>(), It.IsAny<CancellationToken>()))
            .Callback<AgentRun, CancellationToken>((r, _) => capturedRun = r)
            .ReturnsAsync(Guid.NewGuid());

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.SetupGet(client => client.SelectedModel).Returns("qwen3:8b");
        var manager = create_manager(
            llamaClientMock,
            orchestrator: orchestratorMock.Object);

        var result = await manager.ResumeTaskAsync(run);

        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.NotNull(capturedRun);
        Assert.Equal(run.Id, capturedRun!.Id);
        Assert.Equal(run.Prompt, capturedRun.Prompt);
        orchestratorMock.Verify(o => o.ResumeAsync(It.Is<AgentRun>(r => r.Id == run.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task manage_conversation_async_must_retry_recoverable_action_failures_with_previous_failure_context()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage("prompt"));
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Contains("started in background", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task manage_conversation_async_must_stop_when_retry_limit_is_reached()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage("prompt"));
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Contains("started in background", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task manage_conversation_async_must_stop_retries_when_retry_plan_becomes_unsafe()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage("unsafe prompt"));
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Contains("started in background", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task manage_conversation_async_must_cancel_running_action_and_stop_retries()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await manager.ManageConversationAsync(AgentMessage("prompt"), null, cts.Token);
        Assert.True(result.ActionStatus == ActionExecutionStatus.Started || result.ActionStatus == ActionExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task manage_conversation_async_must_stream_action_events_and_log_tool_responses()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage("List files in the current directory."), new InlineProgress<ConversationTurn>(_ => { }), CancellationToken.None);
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Contains("started in background", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task manage_conversation_async_must_stop_incorrect_action_when_retry_limit_is_zero()
    {
        var orchestratorMock = new Mock<IAgentRunOrchestrator>();
        orchestratorMock.Setup(o => o.RunAsync(It.IsAny<AgentActionRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        var manager = create_manager(create_llama_client_mock(), orchestrator: orchestratorMock.Object);
        var result = await manager.ManageConversationAsync(AgentMessage("list files and then create a marker file"));
        Assert.Equal(ActionExecutionStatus.Started, result.ActionStatus);
        Assert.Contains("started in background", result.Response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task manage_response_must_log_mode_and_throw_when_chat_response_fails()
    {
        const string prompt = "list files in the current directory";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Test error"));

        var loggerMock = create_logger_mock();
        var manager = create_manager(llamaClientMock, loggerMock: loggerMock);

#if DEBUG
        var result = await manager.ManageResponse(ChatMessage(prompt));
        Assert.Contains("Erro:", result);
        Assert.Contains("InvalidOperationException", result);
        Assert.Contains("Test error", result);
#else
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ManageResponse(ChatMessage(prompt)));
        Assert.Equal("Test error", exception.Message);
#endif
        loggerMock.Verify(logger => logger.LogError(It.Is<string>(message =>
            message.Contains("[CHAT]", StringComparison.Ordinal) &&
            message.Contains("Error managing response", StringComparison.Ordinal))), Times.Once);
    }

    [Fact]
    public async Task generate_command_steps_must_return_llm_response_when_request_is_valid()
    {
        const string request = "Create a file";
        const string response = """{"Steps":[{"Id":1,"Objective":"Create file","Run":"touch test.txt"}]}""";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.GetResponseAsync(It.IsAny<string>())).ReturnsAsync(response);

        var manager = create_manager(llamaClientMock);

        var result = await manager.GenerateCommandSteps(request);

        Assert.Equal(response, result);
    }

    [Fact]
    public async Task generate_command_steps_must_throw_argument_exception_when_request_is_empty()
    {
        var manager = create_manager(create_llama_client_mock());

        await Assert.ThrowsAsync<ArgumentException>(() => manager.GenerateCommandSteps(string.Empty));
    }

    [Fact]
    public async Task verify_command_correct_async_must_return_true_when_llama_returns_yes()
    {
        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.GetResponseAsync(It.IsAny<string>())).ReturnsAsync("Yes");

        var manager = create_manager(llamaClientMock);
        var result = await manager.VerifyCommandCorrectAsync(new Command { Id = 1, Objective = "Create file", Run = "touch test.txt" });

        Assert.True(result);
    }

    [Fact]
    public async Task verify_command_safety_async_must_return_false_when_policy_blocks()
    {
        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.GetResponseAsync(It.IsAny<string>())).ReturnsAsync("No");

        var manager = create_manager(llamaClientMock);
        var result = await manager.VerifyCommandSafetyAsync(new Command { Id = 1, Objective = "Delete system", Run = "rm -rf /" });

        Assert.False(result);
    }

    [Fact]
    public async Task manage_conversation_async_must_not_wait_indefinitely_when_prompt_persistence_stalls()
    {
        const string prompt = "Explique a arquitetura";
        const string response = "Resposta do mock";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock
            .Setup(client => client.GetResponseAsync(It.Is<string>(text =>
                text.Contains("CHAT MODE", StringComparison.Ordinal) &&
                text.Contains(prompt, StringComparison.Ordinal))))
            .ReturnsAsync(response);

        var promptRepositoryMock = create_prompt_repository_mock();
        promptRepositoryMock
            .Setup(repository => repository.SaveAsync(It.IsAny<PromptRequest>(), It.IsAny<CancellationToken>()))
            .Returns<PromptRequest, CancellationToken>(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new PromptRequest();
            });
        promptRepositoryMock
            .Setup(repository => repository.UpdateResponseAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, CancellationToken>(async (id, savedResponse, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new PromptRequest
                {
                    Id = id,
                    Response = savedResponse
                };
            });

        var loggerMock = create_logger_mock();
        var manager = create_manager(
            llamaClientMock,
            loggerMock: loggerMock,
            promptRepositoryMock: promptRepositoryMock);

        var stopwatch = Stopwatch.StartNew();
        var result = await manager.ManageConversationAsync(ChatMessage(prompt));
        stopwatch.Stop();

        Assert.Equal(response, result.Response);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"The manager took too long to return: {stopwatch.Elapsed}.");
        loggerMock.Verify(logger => logger.LogError(It.Is<string>(message => message.Contains("Timed out while persisting prompt request"))), Times.Once);
        loggerMock.Verify(logger => logger.LogError(It.Is<string>(message => message.Contains("Timed out while updating prompt response"))), Times.Once);
    }

    [Fact]
    public void select_conversation_must_switch_active_conversation_id()
    {
        var manager = create_manager(create_llama_client_mock());
        var targetId = Guid.NewGuid();

        var result = manager.SelectConversation(targetId);

        Assert.Equal(targetId, result);
        Assert.Equal(targetId, manager.ActiveConversationId);
    }

    [Fact]
    public void select_conversation_must_reject_empty_guid()
    {
        var manager = create_manager(create_llama_client_mock());

        Assert.Throws<ArgumentException>(() => manager.SelectConversation(Guid.Empty));
    }

    [Fact]
    public void start_new_conversation_must_reset_active_conversation_id()
    {
        var manager = create_manager(create_llama_client_mock());
        var first = manager.SelectConversation(Guid.NewGuid());

        var second = manager.StartNewConversation();

        Assert.NotEqual(first, second);
        Assert.Equal(second, manager.ActiveConversationId);
    }

    private static Manager create_manager(
        Mock<ILlamaClient> llamaClientMock,
        Mock<IShellExecutor>? executorMock = null,
        Mock<IJsonExtractor>? jsonExtractorMock = null,
        Mock<ILogger>? loggerMock = null,
        Mock<ICommandRepository>? commandRepositoryMock = null,
        Mock<IPromptRequestRepository>? promptRepositoryMock = null,
        IConversationMemoryRepository? conversationMemoryRepository = null,
        int maxActionRetries = 5,
        IAgentActionRunner? actionRunner = null,
        IAgentRunOrchestrator? orchestrator = null)
    {
        return new Manager(
            llamaClientMock.Object,
            (executorMock ?? create_executor_mock()).Object,
            (jsonExtractorMock ?? create_json_extractor_mock()).Object,
            (loggerMock ?? create_logger_mock()).Object,
            commandRepositoryMock?.Object,
            promptRepositoryMock?.Object,
            conversationMemoryRepository,
            maxActionRetries: maxActionRetries,
            actionRunner: actionRunner,
            commandPolicyEngine: create_test_policy_engine(),
            orchestrator: orchestrator);
    }

    private static ICommandPolicyEngine create_test_policy_engine()
    {
        var mock = new Mock<ICommandPolicyEngine>();
        mock.Setup(engine => engine.EvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string command, CancellationToken _) =>
                command.Contains("rm -rf /", StringComparison.OrdinalIgnoreCase)
                    ? new CommandSafetyDecision(
                        CommandSafetyDecisionType.Block,
                        CommandIntent.Blocked,
                        1,
                        ["Blocked by test policy."])
                    : new CommandSafetyDecision(
                        CommandSafetyDecisionType.Allow,
                        CommandIntent.SafeExecuteLocal,
                        1,
                        ["Allowed by test policy."]));
        return mock.Object;
    }

    private static Mock<ILlamaClient> create_llama_client_mock() => new();

    private static Mock<IShellExecutor> create_executor_mock()
    {
        var mock = new Mock<IShellExecutor>();
        mock
            .Setup(executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        return mock;
    }

    private static Mock<IJsonExtractor> create_json_extractor_mock() => new();

    private static Mock<ILogger> create_logger_mock() => new();

    private static Mock<ICommandRepository> create_command_repository_mock() => new();

    private static Mock<IPromptRequestRepository> create_prompt_repository_mock() => new();

    private static UserMessage ChatMessage(string content) =>
        new(content, InteractionMode.Chat);

    private static UserMessage AgentMessage(string content) =>
        new(content, InteractionMode.Agent);

    private static void setup_passthrough_json_extractor(Mock<IJsonExtractor> jsonExtractorMock)
    {
        jsonExtractorMock
            .Setup(extractor => extractor.ExtractJsonObject(It.IsAny<string>()))
            .Returns((string input) => input);
    }

    private static string create_action_decision(
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

    private static string create_complete_decision(
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

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            handler(value);
        }
    }

    private static PromptRequest clone_prompt_request(PromptRequest request)
    {
        return new PromptRequest
        {
            Id = request.Id,
            Prompt = request.Prompt,
            Mode = request.Mode,
            Classification = request.Classification,
            Response = request.Response,
            CreatedAt = request.CreatedAt,
            UpdatedAt = request.UpdatedAt
        };
    }

    private static StoredCommand clone_stored_command(StoredCommand command)
    {
        return new StoredCommand
        {
            Id = command.Id,
            RequestId = command.RequestId,
            CommandId = command.CommandId,
            Objective = command.Objective,
            Command = command.Command,
            OsType = command.OsType,
            Executed = command.Executed,
            ExecutionResult = command.ExecutionResult,
            CreatedAt = command.CreatedAt,
            UpdatedAt = command.UpdatedAt
        };
    }
}
