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
        const string commandJson = """{"Steps":[{"Id":1,"Objective":"List files","Run":"dir"}]}""";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(client => client.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(commandJson)
            .ReturnsAsync("Yes")
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        executorMock.Setup(executor => executor.RunCommandAsync("dir")).ReturnsAsync("Directory listing");

        var jsonExtractorMock = create_json_extractor_mock();
        jsonExtractorMock.Setup(extractor => extractor.ExtractJsonObject(commandJson)).Returns(commandJson);

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
        executorMock.Verify(executor => executor.RunCommandAsync("dir"), Times.Once);
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
        const string commandJson = """{"Steps":[{"Id":1,"Objective":"List files","Run":"dir"}]}""";

        PromptRequest? savedRequest = null;
        Guid updatedRequestId = Guid.Empty;
        string? updatedResponse = null;
        StoredCommand? savedCommand = null;

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(client => client.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(commandJson)
            .ReturnsAsync("Yes")
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        executorMock.Setup(executor => executor.RunCommandAsync("dir")).ReturnsAsync("Directory listing");

        var jsonExtractorMock = create_json_extractor_mock();
        jsonExtractorMock.Setup(extractor => extractor.ExtractJsonObject(commandJson)).Returns(commandJson);

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
    public async Task manage_response_must_not_execute_command_when_action_is_not_safe()
    {
        const string prompt = "Delete system files";
        const string commandJson = """{"Steps":[{"Id":1,"Objective":"Delete files","Run":"rm -rf /"}]}""";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(client => client.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(commandJson)
            .ReturnsAsync("No")
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        var jsonExtractorMock = create_json_extractor_mock();
        jsonExtractorMock.Setup(extractor => extractor.ExtractJsonObject(commandJson)).Returns(commandJson);

        var commandRepositoryMock = create_command_repository_mock();
        commandRepositoryMock
            .Setup(repository => repository.SaveAsync(It.IsAny<StoredCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StoredCommand command, CancellationToken _) => command);
        commandRepositoryMock
            .Setup(repository => repository.SaveVerificationAsync(It.IsAny<CommandVerification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CommandVerification verification, CancellationToken _) => verification);

        var loggerMock = create_logger_mock();

        var manager = create_manager(
            llamaClientMock,
            executorMock,
            jsonExtractorMock,
            loggerMock,
            commandRepositoryMock);

        _ = await manager.ManageResponse(prompt);

        executorMock.Verify(executor => executor.RunCommandAsync(It.IsAny<string>()), Times.Never);
        loggerMock.Verify(logger => logger.LogError(It.Is<string>(message => message.Contains("Command verification failed"))), Times.Once);
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
    public async Task manage_conversation_async_must_fallback_to_chat_when_action_plan_is_invalid()
    {
        const string prompt = "Create a script that says hi.";
        const string invalidPlan = "not-json";
        const string fallbackResponse = "Oi.";

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(client => client.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(invalidPlan)
            .ReturnsAsync(fallbackResponse);

        var jsonExtractorMock = create_json_extractor_mock();
        jsonExtractorMock
            .Setup(extractor => extractor.ExtractJsonObject(invalidPlan))
            .Throws(new ArgumentException("Invalid JSON object."));

        var executorMock = create_executor_mock();
        var manager = create_manager(llamaClientMock, executorMock, jsonExtractorMock);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Equal(ClassificationResult.Chat.ToString(), result.Classification);
        Assert.Equal(fallbackResponse, result.Response);
        Assert.Contains("fallback de chat", result.Reasoning, StringComparison.OrdinalIgnoreCase);
        executorMock.Verify(executor => executor.RunCommandAsync(It.IsAny<string>()), Times.Never);
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
        executorMock.Verify(executor => executor.RunCommandAsync(It.IsAny<string>()), Times.Never);
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
    public async Task manage_conversation_async_must_abort_required_action_chain_after_first_failed_step()
    {
        const string prompt = "list files and then create a marker file";
        const string commandJson = """
            {
                "Steps": [
                    { "Id": 1, "Objective": "List files", "Run": "dir", "Required": true },
                    { "Id": 2, "Objective": "Create marker", "Run": "echo ok > marker.txt", "Required": true }
                ]
            }
            """;

        var llamaClientMock = create_llama_client_mock();
        llamaClientMock.Setup(client => client.ClassifyPrompt(prompt)).ReturnsAsync(ClassificationResult.Action);
        llamaClientMock.SetupSequence(client => client.GetResponseAsync(It.IsAny<string>()))
            .ReturnsAsync(commandJson)
            .ReturnsAsync("No")
            .ReturnsAsync("Yes");

        var executorMock = create_executor_mock();
        var jsonExtractorMock = create_json_extractor_mock();
        jsonExtractorMock.Setup(extractor => extractor.ExtractJsonObject(commandJson)).Returns(commandJson);

        var loggerMock = create_logger_mock();
        var manager = create_manager(
            llamaClientMock,
            executorMock,
            jsonExtractorMock,
            loggerMock);

        var result = await manager.ManageConversationAsync(prompt);

        Assert.Contains("A execucao foi abortada no passo 1", result.Response);
        Assert.Contains("1 passo(s) dependente(s) nao foram executados", result.Response);
        Assert.Equal(2, result.Commands.Count);
        Assert.False(result.Commands[0].Executed);
        Assert.True(result.Commands[1].Skipped);
        executorMock.Verify(executor => executor.RunCommandAsync(It.IsAny<string>()), Times.Never);
        loggerMock.Verify(
            logger => logger.LogError(It.Is<string>(message => message.Contains("Aborting action chain"))),
            Times.Once);
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
        IConversationMemoryRepository? conversationMemoryRepository = null)
    {
        return new Manager(
            llamaClientMock.Object,
            (executorMock ?? create_executor_mock()).Object,
            (jsonExtractorMock ?? create_json_extractor_mock()).Object,
            (loggerMock ?? create_logger_mock()).Object,
            commandRepositoryMock?.Object,
            promptRepositoryMock?.Object,
            conversationMemoryRepository);
    }

    private static Mock<ILlamaClient> create_llama_client_mock() => new();

    private static Mock<IShellExecutor> create_executor_mock() => new();

    private static Mock<IJsonExtractor> create_json_extractor_mock() => new();

    private static Mock<ILogger> create_logger_mock() => new();

    private static Mock<ICommandRepository> create_command_repository_mock() => new();

    private static Mock<IPromptRequestRepository> create_prompt_repository_mock() => new();

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
