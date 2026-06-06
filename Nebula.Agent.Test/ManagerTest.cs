using System.Diagnostics;
using Moq;
using Nebula.Agent.Data;
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

        var result = await manager.ManageResponse(string.Empty);

        Assert.Equal("The prompt are empty, write something.", result);
        llamaClientMock.Verify(client => client.ClassifyPrompt(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task manage_response_must_return_response_when_chat()
    {
        const string prompt = "Hello, how are you?";
        const string response = "I'm doing well, thanks for asking!";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.GetResponseAsync(prompt)).ReturnsAsync(response);

        var manager = create_manager(llamaClientMock);

        var result = await manager.ManageResponse(prompt);

        Assert.Equal(response, result);
        llamaClientMock.Verify(client => client.ClassifyPrompt(prompt), Times.Never);
        llamaClientMock.Verify(client => client.GetResponseAsync(prompt), Times.Once);
    }

    [Fact]
    public async Task manage_response_must_return_response_when_action()
    {
        const string prompt = "list files on c:";
        var actionDecision = create_action_decision("I need to list the files.", "List files", "dir");
        var completeDecision = create_complete_decision(
            "The directory listing was returned.",
            "Directory listing");

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(client => client.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(actionDecision)
            .ReturnsAsync("Yes")
            .ReturnsAsync("Yes")
            .ReturnsAsync(completeDecision);

        var executorMock = create_executor_mock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("dir", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Directory listing");

        var jsonExtractorMock = create_json_extractor_mock();
        setup_passthrough_json_extractor(jsonExtractorMock);

        var commandRepositoryMock = create_command_repository_mock();
        commandRepositoryMock
            .Setup(repository => repository.SaveAsync(It.IsAny<StoredCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StoredCommand command, CancellationToken _) => command);
        commandRepositoryMock
            .Setup(repository => repository.SaveVerificationAsync(It.IsAny<CommandVerification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommandVerification verification, CancellationToken _) => verification);
        commandRepositoryMock
            .Setup(repository => repository.UpdateExecutionAsync(It.IsAny<Guid>(), true, "Directory listing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid commandId, bool executed, string? result, CancellationToken _) => new StoredCommand
            {
                Id = commandId,
                Executed = executed,
                ExecutionResult = result
            });

        var manager = create_manager(
            llamaClientMock,
            executorMock,
            jsonExtractorMock,
            commandRepositoryMock: commandRepositoryMock);

        var result = await manager.ManageResponse(prompt);

        Assert.Equal("Directory listing", result);
        executorMock.Verify(executor => executor.RunCommandAsync("dir", It.IsAny<CancellationToken>()), Times.Once);
        commandRepositoryMock.Verify(repository => repository.SaveAsync(It.IsAny<StoredCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        commandRepositoryMock.Verify(repository => repository.SaveVerificationAsync(It.IsAny<CommandVerification>(), It.IsAny<CancellationToken>()), Times.Once);
        commandRepositoryMock.Verify(repository => repository.UpdateExecutionAsync(It.IsAny<Guid>(), true, "Directory listing", It.IsAny<CancellationToken>()), Times.Once);
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
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Chat);
        llamaClientMock.Setup(client => client.GetResponseAsync(prompt)).ReturnsAsync(response);

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

        var result = await manager.ManageResponse(prompt);

        Assert.Equal(response, result);
        Assert.NotNull(savedRequest);
        Assert.Equal(prompt, savedRequest!.Prompt);
        Assert.Equal(ClassificationResult.Chat.ToString(), savedRequest.Classification);
        Assert.Equal(savedRequest.Id, updatedRequestId);
        Assert.Equal(response, updatedResponse);
    }

    [Fact]
    public async Task manage_response_must_save_prompt_and_response_when_action()
    {
        const string prompt = "list files on c:";
        var actionDecision = create_action_decision("I need to list the files.", "List files", "dir");
        var completeDecision = create_complete_decision(
            "The directory listing was returned.",
            "Directory listing");

        PromptRequest? savedRequest = null;
        Guid updatedRequestId = Guid.Empty;
        string? updatedResponse = null;
        StoredCommand? savedCommand = null;

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(client => client.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(actionDecision)
            .ReturnsAsync("Yes")
            .ReturnsAsync("Yes")
            .ReturnsAsync(completeDecision);

        var executorMock = create_executor_mock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("dir", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Directory listing");

        var jsonExtractorMock = create_json_extractor_mock();
        setup_passthrough_json_extractor(jsonExtractorMock);

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

        var commandRepositoryMock = create_command_repository_mock();
        commandRepositoryMock
            .Setup(repository => repository.SaveAsync(It.IsAny<StoredCommand>(), It.IsAny<CancellationToken>()))
            .Callback<StoredCommand, CancellationToken>((command, _) => savedCommand = clone_stored_command(command))
            .ReturnsAsync((StoredCommand command, CancellationToken _) => command);
        commandRepositoryMock
            .Setup(repository => repository.SaveVerificationAsync(It.IsAny<CommandVerification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommandVerification verification, CancellationToken _) => verification);
        commandRepositoryMock
            .Setup(repository => repository.UpdateExecutionAsync(It.IsAny<Guid>(), true, "Directory listing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid commandId, bool executed, string? result, CancellationToken _) => new StoredCommand
            {
                Id = commandId,
                Executed = executed,
                ExecutionResult = result
            });

        var manager = create_manager(
            llamaClientMock,
            executorMock,
            jsonExtractorMock,
            commandRepositoryMock: commandRepositoryMock,
            promptRepositoryMock: promptRepositoryMock);

        var result = await manager.ManageResponse(prompt);

        Assert.Equal("Directory listing", result);
        Assert.NotNull(savedRequest);
        Assert.NotNull(savedCommand);
        Assert.Equal(savedRequest!.Id, savedCommand!.RequestId);
        Assert.Equal(ClassificationResult.Action.ToString(), savedRequest.Classification);
        Assert.Equal(savedRequest.Id, updatedRequestId);
        Assert.Equal("Directory listing", updatedResponse);
    }

    [Fact]
    public async Task manage_conversation_async_must_block_unsafe_action_before_planning()
    {
        const string prompt = "Delete system files";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);

        var executorMock = create_executor_mock();
        var jsonExtractorMock = create_json_extractor_mock();

        var commandRepositoryMock = create_command_repository_mock();

        var loggerMock = create_logger_mock();

        var manager = create_manager(
            llamaClientMock,
            executorMock,
            jsonExtractorMock,
            loggerMock,
            commandRepositoryMock);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Equal(ActionExecutionStatus.Unsafe, result.ActionStatus);
        Assert.Contains("bloqueada", result.Response, StringComparison.OrdinalIgnoreCase);
        llamaClientMock.Verify(client => client.GetResponseAsync(
            It.IsAny<string>(),
            It.IsAny<IProgress<LlamaStreamUpdate>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        jsonExtractorMock.Verify(extractor => extractor.ExtractJsonObject(It.IsAny<string>()), Times.Never);
        executorMock.Verify(executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task manage_conversation_async_must_extract_reasoning_when_model_returns_think_block()
    {
        const string prompt = "Explain Nebula";
        const string rawResponse = "<think>Analyzing the request</think>Nebula is a local AI assistant.";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Chat);
        llamaClientMock.Setup(client => client.GetResponseAsync(prompt)).ReturnsAsync(rawResponse);

        var manager = create_manager(llamaClientMock);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Equal("Nebula is a local AI assistant.", result.Response);
        Assert.Equal("Analyzing the request", result.Reasoning);
        Assert.Equal(ClassificationResult.Chat.ToString(), result.Classification);
    }

    [Fact]
    public async Task manage_conversation_async_must_report_streaming_updates_for_chat()
    {
        const string prompt = "Explain the flow";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Chat);
        llamaClientMock
            .Setup(client => client.GetResponseAsync(prompt, It.IsAny<IProgress<LlamaStreamUpdate>?>(), It.IsAny<CancellationToken>()))
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

        var result = await manager.ManageConversationAsync(prompt, progress, CancellationToken.None);

        Assert.NotNull(partialUpdate);
        Assert.Equal("Partial response", partialUpdate!.Response);
        Assert.Equal("Partial reasoning", partialUpdate.Reasoning);
        Assert.Equal("Final response", result.Response);
        Assert.Equal("Partial reasoning", result.Reasoning);
    }

    [Fact]
    public async Task manage_conversation_async_must_report_failure_when_action_plan_is_invalid()
    {
        const string prompt = "Create a script that says hi.";
        const string invalidPlan = "not-json";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(invalidPlan);

        var jsonExtractorMock = create_json_extractor_mock();
        jsonExtractorMock
            .Setup(extractor => extractor.ExtractJsonObject(invalidPlan))
            .Throws(new ArgumentException("Invalid JSON object."));

        var executorMock = create_executor_mock();
        var manager = create_manager(llamaClientMock, executorMock, jsonExtractorMock, maxActionRetries: 0);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Equal(ClassificationResult.Action.ToString(), result.Classification);
        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Contains("Limite de retry por passo", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid", result.Reasoning, StringComparison.OrdinalIgnoreCase);
        executorMock.Verify(executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task manage_conversation_async_must_downgrade_non_operational_action_prompt_to_chat()
    {
        const string prompt = "Diga oi em uma linha.";
        const string response = "Oi.";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.Setup(client => client.GetResponseAsync(prompt)).ReturnsAsync(response);

        var executorMock = create_executor_mock();
        var manager = create_manager(llamaClientMock, executorMock);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Equal(ClassificationResult.Chat.ToString(), result.Classification);
        Assert.Equal(response, result.Response);
        executorMock.Verify(executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task manage_conversation_async_must_bypass_model_classifier_for_clear_chat_prompts()
    {
        const string prompt = "Ola";
        const string response = "Oi!";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.GetResponseAsync(prompt)).ReturnsAsync(response);

        var loggerMock = create_logger_mock();
        var manager = create_manager(llamaClientMock, loggerMock: loggerMock);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Equal(ClassificationResult.Chat.ToString(), result.Classification);
        Assert.Equal(response, result.Response);
        llamaClientMock.Verify(client => client.ClassifyPrompt(prompt), Times.Never);
        loggerMock.Verify(logger => logger.Log(It.Is<string>(message => message.Contains("classified locally as chat", StringComparison.OrdinalIgnoreCase))), Times.Once);
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

        var firstTurn = await manager.ManageConversationAsync("Explique Nebula");
        var secondTurn = await manager.ManageConversationAsync("Agora explique ela em uma linha");

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
        const string firstPrompt = "Remember that the project name is Nebula.";
        const string actionPrompt = "Create a script that prints that project name.";
        var decisions = new Queue<string>(
        [
            create_action_decision(
                "I need to create the script with the remembered project name.",
                "Create script",
                "echo Nebula > project.py"),
            create_complete_decision(
                "The file creation observation confirms completion.",
                "created")
        ]);

        var capturedPlanningPrompts = new List<string>();
        var memoryRepository = new InMemoryConversationMemoryRepository();

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.GetResponseAsync(It.Is<string>(prompt =>
                !prompt.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase) &&
                !prompt.Contains("Response only", StringComparison.OrdinalIgnoreCase))))
            .ReturnsAsync("I will remember Nebula.");
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IProgress<LlamaStreamUpdate>?, CancellationToken>((prompt, _, _) => capturedPlanningPrompts.Add(prompt))
            .ReturnsAsync(() => decisions.Dequeue());
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(prompt => prompt.Contains("Response only", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Yes");
        llamaClientMock.Setup(client => client.ClassifyPrompt(actionPrompt)).ReturnsAsync(ClassificationResult.Action);

        var executorMock = create_executor_mock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("echo Nebula > project.py", It.IsAny<CancellationToken>()))
            .ReturnsAsync("created");

        var jsonExtractorMock = create_json_extractor_mock();
        setup_passthrough_json_extractor(jsonExtractorMock);

        var manager = create_manager(
            llamaClientMock,
            executorMock,
            jsonExtractorMock,
            conversationMemoryRepository: memoryRepository);

        await manager.ManageConversationAsync(firstPrompt);
        var result = await manager.ManageConversationAsync(actionPrompt);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal(2, capturedPlanningPrompts.Count);
        Assert.All(capturedPlanningPrompts, planningPrompt =>
        {
            Assert.Contains("[recent_messages]", planningPrompt);
            Assert.Contains(firstPrompt, planningPrompt);
            Assert.Contains("assistant: I will remember Nebula.", planningPrompt);
            Assert.Contains(actionPrompt, planningPrompt);
        });
    }

    [Fact]
    public async Task manage_conversation_async_must_retry_recoverable_action_failures_with_previous_failure_context()
    {
        const string prompt = "Create a marker file.";
        var planningPrompts = new List<string>();
        var decisions = new Queue<string>(
        [
            create_action_decision("I need to create the marker.", "Create marker", "badcmd"),
            create_action_decision(
                "The first command failed, so I need to use the corrected command.",
                "Create marker with corrected command",
                "goodcmd"),
            create_complete_decision("The corrected command succeeded.", "ok")
        ]);

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IProgress<LlamaStreamUpdate>?, CancellationToken>((prompt, _, _) => planningPrompts.Add(prompt))
            .ReturnsAsync(() => decisions.Dequeue());
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("Response only", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bad command"));
        executorMock
            .Setup(executor => executor.RunCommandAsync("goodcmd", It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");

        var jsonExtractorMock = create_json_extractor_mock();
        jsonExtractorMock
            .Setup(extractor => extractor.ExtractJsonObject(It.IsAny<string>()))
            .Returns((string input) => input);

        var manager = create_manager(llamaClientMock, executorMock, jsonExtractorMock);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Equal(ActionExecutionStatus.Completed, result.ActionStatus);
        Assert.Equal("ok", result.Response);
        Assert.Contains(result.ActionEvents, actionEvent => actionEvent.Status == ActionExecutionStatus.Retrying);
        Assert.Contains(result.Commands, command => command.Attempt == 1 && command.Error == "bad command");
        Assert.Contains(result.Commands, command => command.Attempt == 2 && command.Executed);
        Assert.Equal(3, planningPrompts.Count);
        Assert.Contains("Previous action result", planningPrompts[1]);
        Assert.Contains("bad command", planningPrompts[1]);
        executorMock.Verify(executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()), Times.Once);
        executorMock.Verify(executor => executor.RunCommandAsync("goodcmd", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task manage_conversation_async_must_stop_when_retry_limit_is_reached()
    {
        const string prompt = "Create a marker file.";
        var decisions = new Queue<string>(
        [
            create_action_decision("I need to create the marker.", "Create marker", "badcmd"),
            create_action_decision("The command failed, so I need to retry it.", "Retry marker", "badcmd")
        ]);

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => decisions.Dequeue());
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("Response only", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("still failing"));

        var jsonExtractorMock = create_json_extractor_mock();
        setup_passthrough_json_extractor(jsonExtractorMock);

        var manager = create_manager(llamaClientMock, executorMock, jsonExtractorMock, maxActionRetries: 1);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Equal(ActionExecutionStatus.Failed, result.ActionStatus);
        Assert.Contains("Limite de retry por passo (1)", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, result.Commands.Count(command => command.Run == "badcmd"));
        executorMock.Verify(executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task manage_conversation_async_must_stop_retries_when_retry_plan_becomes_unsafe()
    {
        const string prompt = "Create a marker file.";
        var decisions = new Queue<string>(
        [
            create_action_decision("I need to create the marker.", "Create marker", "badcmd"),
            create_action_decision(
                "The first command failed, so I need to try a different command.",
                "Create marker",
                "rm -rf /")
        ]);

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => decisions.Dequeue());
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("Response only", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bad command"));

        var jsonExtractorMock = create_json_extractor_mock();
        jsonExtractorMock
            .Setup(extractor => extractor.ExtractJsonObject(It.IsAny<string>()))
            .Returns((string input) => input);

        var manager = create_manager(llamaClientMock, executorMock, jsonExtractorMock);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Equal(ActionExecutionStatus.Unsafe, result.ActionStatus);
        Assert.Contains("inseguro", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Commands, command => command.Attempt == 2 && command.Run == "rm -rf /" && !command.PassedLocalSafety);
        Assert.Empty(decisions);
        executorMock.Verify(executor => executor.RunCommandAsync("badcmd", It.IsAny<CancellationToken>()), Times.Once);
        executorMock.Verify(executor => executor.RunCommandAsync("rm -rf /", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task manage_conversation_async_must_cancel_running_action_and_stop_retries()
    {
        const string prompt = "Create a marker file.";
        var actionDecision = create_action_decision(
            "I need to run the long-running command.",
            "Create marker",
            "slowcmd");

        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationSource = new CancellationTokenSource();

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(actionDecision);
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("Response only", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("slowcmd", It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                commandStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "never";
            });

        var jsonExtractorMock = create_json_extractor_mock();
        setup_passthrough_json_extractor(jsonExtractorMock);

        var manager = create_manager(llamaClientMock, executorMock, jsonExtractorMock);
        var task = manager.ManageConversationAsync(prompt, progress: null, cancellationSource.Token);

        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellationSource.Cancel();

        var result = await task;

        Assert.True(result.IsCancelled);
        Assert.Equal(ActionExecutionStatus.Cancelled, result.ActionStatus);
        Assert.Contains("cancelada", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.ActionEvents, actionEvent => actionEvent.Status == ActionExecutionStatus.Cancelled);
        executorMock.Verify(executor => executor.RunCommandAsync("slowcmd", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task manage_conversation_async_must_stream_action_events_and_log_tool_responses()
    {
        const string prompt = "List files in the current directory.";
        var decisions = new Queue<string>(
        [
            create_action_decision("I need to inspect the directory.", "List files", "dir"),
            create_complete_decision("The listing was returned.", "Directory listing")
        ]);

        var updates = new List<ConversationTurn>();

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("ReAct action controller", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => decisions.Dequeue());
        llamaClientMock.Setup(client => client.GetResponseAsync(
                It.Is<string>(text => text.Contains("Response only", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        executorMock
            .Setup(executor => executor.RunCommandAsync("dir", It.IsAny<CancellationToken>()))
            .ReturnsAsync("Directory listing");

        var jsonExtractorMock = create_json_extractor_mock();
        setup_passthrough_json_extractor(jsonExtractorMock);

        var manager = create_manager(llamaClientMock, executorMock, jsonExtractorMock);
        var progress = new InlineProgress<ConversationTurn>(updates.Add);

        var result = await manager.ManageConversationAsync(prompt, progress, CancellationToken.None);

        var streamedStatuses = updates
            .Select(update => update.ActionStatus)
            .Where(status => status is not null)
            .Select(status => status!.Value)
            .ToHashSet();

        Assert.Contains(ActionExecutionStatus.Validating, streamedStatuses);
        Assert.Contains(ActionExecutionStatus.Planning, streamedStatuses);
        Assert.Contains(ActionExecutionStatus.Executing, streamedStatuses);
        Assert.Contains(ActionExecutionStatus.Completed, streamedStatuses);
        Assert.Contains(result.ActionEvents, actionEvent =>
            actionEvent.Kind == ActionExecutionEventKind.ActionStarted &&
            actionEvent.Command == "dir");
        Assert.Contains(result.ActionEvents, actionEvent =>
            actionEvent.Kind == ActionExecutionEventKind.Observation &&
            actionEvent.ToolResponse == "Directory listing");
        Assert.Contains("Directory listing", result.Reasoning);
    }

    [Fact]
    public async Task manage_conversation_async_must_stop_incorrect_action_when_retry_limit_is_zero()
    {
        const string prompt = "list files and then create a marker file";
        var actionDecision = create_action_decision(
            "I need to list the files first.",
            "List files",
            "dir");

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(client => client.GetResponseAsync(
                It.IsAny<string>(),
                It.IsAny<IProgress<LlamaStreamUpdate>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(actionDecision)
            .ReturnsAsync("No")
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        var jsonExtractorMock = create_json_extractor_mock();
        setup_passthrough_json_extractor(jsonExtractorMock);

        var loggerMock = create_logger_mock();
        var manager = create_manager(
            llamaClientMock,
            executorMock,
            jsonExtractorMock,
            loggerMock,
            maxActionRetries: 0);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Contains("Limite de retry por passo (0)", result.Response, StringComparison.OrdinalIgnoreCase);
        Assert.Single(result.Commands);
        Assert.False(result.Commands[0].Executed);
        executorMock.Verify(executor => executor.RunCommandAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task manage_response_must_log_and_throw_when_classification_throws()
    {
        const string prompt = "list files in the current directory";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ThrowsAsync(new InvalidOperationException("Test error"));

        var loggerMock = create_logger_mock();
        var manager = create_manager(llamaClientMock, loggerMock: loggerMock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ManageResponse(prompt));

        Assert.Equal("Test error", exception.Message);
        loggerMock.Verify(logger => logger.LogError(It.Is<string>(message => message.Contains("Error managing response"))), Times.Once);
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
    public async Task verify_command_safety_async_must_return_false_when_llama_returns_no()
    {
        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.GetResponseAsync(It.IsAny<string>())).ReturnsAsync("No");

        var manager = create_manager(llamaClientMock);
        var result = await manager.VerifyCommandSafetyAsync(new Command { Id = 1, Objective = "Delete file", Run = "rm test.txt" });

        Assert.False(result);
    }

    [Fact]
    public async Task manage_conversation_async_must_not_wait_indefinitely_when_prompt_persistence_stalls()
    {
        const string prompt = "Explique a arquitetura";
        const string response = "Resposta do mock";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Chat);
        llamaClientMock.Setup(client => client.GetResponseAsync(prompt)).ReturnsAsync(response);

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
        var result = await manager.ManageConversationAsync(prompt);
        stopwatch.Stop();

        Assert.Equal(response, result.Response);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"The manager took too long to return: {stopwatch.Elapsed}.");
        loggerMock.Verify(logger => logger.LogError(It.Is<string>(message => message.Contains("Timed out while persisting prompt request"))), Times.Once);
        loggerMock.Verify(logger => logger.LogError(It.Is<string>(message => message.Contains("Timed out while updating prompt response"))), Times.Once);
    }

    private static Manager create_manager(
        Mock<ILlamaClient> llamaClientMock,
        Mock<IShellExecutor>? executorMock = null,
        Mock<IJsonExtractor>? jsonExtractorMock = null,
        Mock<ILogger>? loggerMock = null,
        Mock<ICommandRepository>? commandRepositoryMock = null,
        Mock<IPromptRequestRepository>? promptRepositoryMock = null,
        IConversationMemoryRepository? conversationMemoryRepository = null,
        int maxActionRetries = 5)
    {
        return new Manager(
            llamaClientMock.Object,
            (executorMock ?? create_executor_mock()).Object,
            (jsonExtractorMock ?? create_json_extractor_mock()).Object,
            (loggerMock ?? create_logger_mock()).Object,
            commandRepositoryMock?.Object,
            promptRepositoryMock?.Object,
            conversationMemoryRepository,
            maxActionRetries: maxActionRetries);
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
