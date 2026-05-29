//-----------------------------------------------------------------------
// <copyright file="Manager.cs" company="Starlight-Technology">
//     Author:
//     Copyright (c) Starlight-Technology. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Nebula.Agent.Data;
using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.Agent;

public class Manager(
    ILlamaClient llamaClient,
    IShellExecutor executor,
    IJsonExtractor jsonExtractor,
    ILogger logger,
    ICommandRepository? commandRepository = null,
    IPromptRequestRepository? promptRepository = null,
    IConversationMemoryRepository? conversationMemoryRepository = null,
    NebulaContextBuilder? contextBuilder = null) : IManager
{
    private static readonly TimeSpan PromptPersistenceTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ConversationMemoryTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly NebulaContextBuilder nebulaContextBuilder = contextBuilder ?? new NebulaContextBuilder();
    private Guid activeConversationId = Guid.NewGuid();
    private Guid currentRequestId = Guid.NewGuid();

    public Guid ActiveConversationId => activeConversationId;

    public async Task<string> ManageResponse(string prompt)
    {
        var turn = await ManageConversationAsync(prompt);
        return turn.Response;
    }

    public Task<ConversationTurn> ManageConversationAsync(string prompt)
    {
        return ManageConversationAsync(prompt, progress: null, cancellationToken: default);
    }

    public Guid StartNewConversation()
    {
        activeConversationId = Guid.NewGuid();
        logger.Log($"Started new ConversationId '{activeConversationId}'.");

        return activeConversationId;
    }

    public async Task<ConversationTurn> ManageConversationAsync(
        string prompt,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new ConversationTurn
                {
                    ConversationId = activeConversationId,
                    RequestId = Guid.Empty,
                    Prompt = prompt,
                    ModelName = llamaClient.SelectedModel,
                    Classification = ClassificationResult.Unknown.ToString(),
                    Response = "The prompt are empty, write something."
                };
            }

            currentRequestId = Guid.NewGuid();
            var conversationId = activeConversationId;
            logger.Log($"Using ConversationId '{conversationId}' for request '{currentRequestId}'.");

            var userMessage = await TryAddConversationMessageAsync(new ConversationMessage
            {
                ConversationId = conversationId,
                Role = ConversationRoles.User,
                Content = prompt.Trim()
            }, cancellationToken);

            var recentMessages = await TryGetRecentMessagesAsync(
                conversationId,
                NebulaContextBuilder.DefaultRecentMessageLimit,
                cancellationToken);
            var conversationState = await TryGetConversationStateAsync(conversationId, cancellationToken);

            logger.Log(
                $"ConversationId '{conversationId}' loaded {recentMessages.Count} recent message(s). " +
                $"Conversation state: {(conversationState is null ? "missing" : "loaded")}.");

            var modelPrompt = conversationMemoryRepository is null
                ? prompt
                : nebulaContextBuilder.Build(conversationId, conversationState, recentMessages, userMessage);

            var looksOperational = LooksLikeComputerOperationPrompt(prompt);
            var classification = looksOperational
                ? await llamaClient.ClassifyPrompt(prompt)
                : ClassificationResult.Chat;

            if (!looksOperational)
            {
                logger.Log($"Prompt '{prompt}' was classified locally as chat before calling the model classifier.");
            }
            else if (classification == ClassificationResult.Unknown)
            {
                classification = looksOperational
                    ? ClassificationResult.Action
                    : ClassificationResult.Chat;

                logger.Log($"Prompt '{prompt}' received an unknown model classification and was mapped locally to {classification}.");
            }

            if (classification == ClassificationResult.Action && !looksOperational)
            {
                logger.Log($"Prompt '{prompt}' was downgraded from action to chat by the local guardrail.");
                classification = ClassificationResult.Chat;
            }

            var promptRequest = new PromptRequest
            {
                Id = currentRequestId,
                Prompt = prompt,
                Classification = classification.ToString()
            };

            await TrySavePromptRequestAsync(promptRequest, cancellationToken);

            var turn = classification switch
            {
                ClassificationResult.Action => await HandleActionAsync(prompt, modelPrompt, cancellationToken),
                ClassificationResult.Chat => await HandleChatAsync(prompt, modelPrompt, progress, cancellationToken),
                _ => new ConversationTurn
                {
                    ConversationId = conversationId,
                    RequestId = currentRequestId,
                    Prompt = prompt,
                    ModelName = llamaClient.SelectedModel,
                    Classification = ClassificationResult.Unknown.ToString(),
                    Response = "Unable to classify the prompt. Please try again with a clearer request."
                }
            };

            turn.ConversationId = conversationId;

            promptRequest.Response = turn.Response;
            promptRequest.UpdatedAt = DateTime.UtcNow;

            await TryUpdatePromptResponseAsync(currentRequestId, turn.Response, cancellationToken);
            await TryAddConversationMessageAsync(new ConversationMessage
            {
                ConversationId = conversationId,
                Role = ConversationRoles.Assistant,
                Content = turn.Response
            }, cancellationToken);
            await TryUpsertConversationStateAsync(
                BuildUpdatedConversationState(conversationId, conversationState, prompt, turn),
                cancellationToken);

            return turn;
        }
        catch (Exception ex)
        {
            logger.LogError($"Error managing response: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> VerifyCommandCorrectAsync(Command command)
    {
        var response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". This following command {{command.Run}} execute EXACTLY {{command.Objective}} on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}? Command: {{command.Run}}
            """);

        return IsAffirmativeResponse(response);
    }

    public async Task<bool> VerifyCommandSafetyAsync(Command command)
    {
        var response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". Is the following command safe to execute on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}? Command: {{command.Run}}
            """);

        return IsAffirmativeResponse(response);
    }

    public async Task<string> GenerateCommandSteps(string userRequest)
    {
        return await GenerateCommandSteps(userRequest, CancellationToken.None);
    }

    private async Task<string> GenerateCommandSteps(string userRequest, CancellationToken cancellationToken)
    {
        return await GenerateCommandSteps(userRequest, userRequest, cancellationToken);
    }

    private async Task<string> GenerateCommandSteps(
        string userRequest,
        string conversationContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userRequest))
        {
            throw new ArgumentException("User request cannot be null or empty.", nameof(userRequest));
        }

        var payloadPrompt = $$"""
                        You are a command planner.

                        Your job:
                        - Convert the user request into a sequence of shell commands on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}.
                        - Each command must be a step to be executed on terminal only.
                        - Use the conversation context to resolve references to previous messages.
                        - Mark Required as true unless a step is optional and later steps do not depend on it.
                        - Respond ONLY in valid JSON.
                        - Do NOT add explanations, comments or extra text.

                        Response format:
                        {
                            "Steps": [
                                { "Id": 1, "Objective": "why this command", "Run": "first shell command here", "Required": true }
                            ]
                        }
                        Add more steps if needed, but keep the JSON format.

                        Conversation context:
                        {{conversationContext}}

                        User request:
                        {{userRequest}}
                        """;

        return await llamaClient.GetResponseAsync(payloadPrompt);
    }

    private async Task<ConversationTurn> HandleChatAsync(
        string prompt,
        string modelPrompt,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken)
    {
        var streamingProgress = progress is null
            ? null
            : new InlineProgress<LlamaStreamUpdate>(update =>
            {
                progress.Report(new ConversationTurn
                {
                    ConversationId = activeConversationId,
                    RequestId = currentRequestId,
                    Prompt = prompt,
                    ModelName = llamaClient.SelectedModel,
                    Classification = ClassificationResult.Chat.ToString(),
                    Response = update.Response,
                    Reasoning = string.IsNullOrWhiteSpace(update.Reasoning) ? null : update.Reasoning
                });
            });

        var rawResponse = progress is null
            ? await llamaClient.GetResponseAsync(modelPrompt)
            : await llamaClient.GetResponseAsync(modelPrompt, streamingProgress, cancellationToken);
        var parsedResponse = ModelResponse.Parse(rawResponse);

        return new ConversationTurn
        {
            ConversationId = activeConversationId,
            RequestId = currentRequestId,
            Prompt = prompt,
            ModelName = llamaClient.SelectedModel,
            Classification = ClassificationResult.Chat.ToString(),
            Response = string.IsNullOrWhiteSpace(parsedResponse.Response)
                ? "Nao consegui gerar uma resposta para esse pedido."
                : parsedResponse.Response,
            Reasoning = string.IsNullOrWhiteSpace(parsedResponse.Reasoning) ? null : parsedResponse.Reasoning
        };
    }

    private async Task<ConversationTurn> HandleActionAsync(string prompt, string modelPrompt, CancellationToken cancellationToken)
    {
        try
        {
            var commandsResponse = await GenerateCommandSteps(prompt, modelPrompt, cancellationToken);
            var parsedPlan = ModelResponse.Parse(commandsResponse);
            var responsePayload = string.IsNullOrWhiteSpace(parsedPlan.Response)
                ? commandsResponse
                : parsedPlan.Response;

            var json = ExtractJsonObject(responsePayload);
            var wrapper = JsonSerializer.Deserialize<CommandSteps>(json);
            var plannedCommands = wrapper?.Steps ?? [];
            var executedCommands = new List<CommandExecution>();
            CommandExecution? failedRequiredStep = null;

            foreach (var command in plannedCommands)
            {
                if (failedRequiredStep is not null)
                {
                    var skippedExecution = CreateSkippedExecution(command, failedRequiredStep);
                    executedCommands.Add(skippedExecution);
                    logger.Log(
                        $"Skipping step '{command.Id}' because required step '{failedRequiredStep.Id}' failed in ConversationId '{activeConversationId}'.");
                    continue;
                }

                var execution = await ExecuteCommandAsync(command);
                executedCommands.Add(execution);

                if (execution.Required && !execution.Executed)
                {
                    failedRequiredStep = execution;
                    logger.LogError(
                        $"Aborting action chain for ConversationId '{activeConversationId}' at required step '{execution.Id}': {execution.Notes}");
                }
            }

            return new ConversationTurn
            {
                ConversationId = activeConversationId,
                RequestId = currentRequestId,
                Prompt = prompt,
                ModelName = llamaClient.SelectedModel,
                Classification = ClassificationResult.Action.ToString(),
                Response = BuildActionResponse(executedCommands),
                Reasoning = BuildActionReasoning(parsedPlan.Reasoning, executedCommands),
                Commands = executedCommands
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            logger.LogError($"Invalid action plan returned by model '{llamaClient.SelectedModel}': {ex.Message}");

            var fallback = await HandleChatAsync(prompt, modelPrompt, progress: null, cancellationToken);
            fallback.Reasoning = BuildActionFallbackReasoning(fallback.Reasoning, ex.Message);
            return fallback;
        }
    }

    private async Task<CommandExecution> ExecuteCommandAsync(Command command)
    {
        var execution = new CommandExecution
        {
            Id = command.Id,
            Objective = command.Objective,
            Run = command.Run,
            Required = command.Required
        };

        var storedCommand = await TrySaveCommandAsync(command);

        execution.IsCorrect = await VerifyCommandCorrectAsync(command);
        execution.IsSafe = await VerifyCommandSafetyAsync(command);
        execution.PassedLocalSafety = PlatformDetector.IsCommandContentSafe(command.Run);
        execution.Notes = BuildVerificationNotes(execution);

        await TrySaveVerificationAsync(storedCommand?.Id, execution);

        if (!(execution.IsCorrect && execution.IsSafe && execution.PassedLocalSafety))
        {
            logger.LogError($"Command verification failed for '{command.Run}'.");
            return execution;
        }

        try
        {
            var result = await executor.RunCommandAsync(command.Run);

            execution.Executed = true;
            execution.Output = result;
            execution.Notes = string.IsNullOrWhiteSpace(result)
                ? "Comando executado sem saida textual."
                : "Comando executado com sucesso.";

            logger.Log(result);
            await TryUpdateExecutionAsync(storedCommand?.Id, true, result);
        }
        catch (Exception ex)
        {
            execution.Notes = $"Falha ao executar o comando: {ex.Message}";
            logger.LogError(execution.Notes);
            await TryUpdateExecutionAsync(storedCommand?.Id, false, execution.Notes);
        }

        return execution;
    }

    private static CommandExecution CreateSkippedExecution(Command command, CommandExecution failedRequiredStep)
    {
        return new CommandExecution
        {
            Id = command.Id,
            Objective = command.Objective,
            Run = command.Run,
            Required = command.Required,
            Skipped = true,
            Notes = $"Passo nao executado porque o passo obrigatorio {failedRequiredStep.Id} falhou."
        };
    }

    private string ExtractJsonObject(string input)
    {
        try
        {
            return jsonExtractor.ExtractJsonObject(input);
        }
        catch (ArgumentException ex)
        {
            logger.LogError($"Error extracting JSON: {ex.Message}");
            throw;
        }
    }

    private static bool IsAffirmativeResponse(string rawResponse)
    {
        var response = ModelResponse.Parse(rawResponse).Response.Trim();

        return Regex.IsMatch(response, @"^yes\b", RegexOptions.IgnoreCase);
    }

    private static string BuildActionResponse(IReadOnlyList<CommandExecution> commands)
    {
        if (commands.Count == 0)
        {
            return "Nao consegui gerar passos executaveis para esse pedido.";
        }

        var failedStep = commands.FirstOrDefault(command => !command.Executed && !command.Skipped);
        if (failedStep is not null)
        {
            var skippedCount = commands.Count(command => command.Skipped);
            var abortMessage =
                $"A execucao foi abortada no passo {failedStep.Id} ({failedStep.Objective}). {failedStep.Notes}";

            return skippedCount > 0
                ? $"{abortMessage} {skippedCount} passo(s) dependente(s) nao foram executados."
                : abortMessage;
        }

        var outputs = commands
            .Where(command => command.Executed && !string.IsNullOrWhiteSpace(command.Output))
            .Select(command => command.Output!.Trim())
            .ToList();

        if (outputs.Count > 0)
        {
            return string.Join(Environment.NewLine + Environment.NewLine, outputs);
        }

        if (commands.Any(command => command.Executed))
        {
            return "Os comandos foram executados, mas nao retornaram saida textual.";
        }

        var blockedNotes = commands
            .Select(command => command.Notes)
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .ToList();

        return blockedNotes.Count > 0
            ? string.Join(Environment.NewLine, blockedNotes)
            : "Os passos planejados foram bloqueados pela verificacao de seguranca.";
    }

    private static string BuildActionReasoning(string? modelReasoning, IReadOnlyList<CommandExecution> commands)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(modelReasoning))
        {
            builder.AppendLine(modelReasoning.Trim());
            builder.AppendLine();
        }

        builder.AppendLine($"Planejei {commands.Count} passo(s) para atender ao pedido.");

        if (commands.Count == 0)
        {
            return builder.ToString().Trim();
        }

        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];

            builder.AppendLine();
            builder.AppendLine($"{index + 1}. {command.Objective}");
            builder.AppendLine($"   Comando: {command.Run}");
            builder.AppendLine($"   Obrigatorio: {(command.Required ? "sim" : "nao")}");

            if (!command.Skipped)
            {
                builder.AppendLine($"   Corretude: {(command.IsCorrect ? "sim" : "nao")}");
                builder.AppendLine($"   Seguranca do modelo: {(command.IsSafe ? "sim" : "nao")}");
                builder.AppendLine($"   Seguranca local: {(command.PassedLocalSafety ? "sim" : "nao")}");
            }

            builder.AppendLine($"   Status: {GetCommandStatus(command)}");

            if (!string.IsNullOrWhiteSpace(command.Notes))
            {
                builder.AppendLine($"   Observacao: {command.Notes}");
            }
        }

        return builder.ToString().Trim();
    }

    private static string GetCommandStatus(CommandExecution command)
    {
        if (command.Skipped)
        {
            return "nao executado por dependencia";
        }

        return command.Executed ? "executado" : "bloqueado";
    }

    private static string BuildActionFallbackReasoning(string? chatReasoning, string error)
    {
        var builder = new StringBuilder();
        builder.AppendLine("O modelo tentou planejar uma acao, mas nao retornou JSON valido para os passos.");
        builder.AppendLine($"Motivo tecnico: {error}");
        builder.AppendLine();
        builder.AppendLine("A resposta abaixo foi gerada em fallback de chat para evitar falha do turno.");

        if (!string.IsNullOrWhiteSpace(chatReasoning))
        {
            builder.AppendLine();
            builder.AppendLine(chatReasoning.Trim());
        }

        return builder.ToString().Trim();
    }

    private static bool LooksLikeComputerOperationPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var normalized = prompt.Trim().ToLowerInvariant();
        string[] actionKeywords =
        [
            "arquivo",
            "arquivos",
            "pasta",
            "pastas",
            "diretorio",
            "diretorios",
            "terminal",
            "comando",
            "comandos",
            "shell",
            "powershell",
            "bash",
            "cmd",
            "git",
            "docker",
            "script",
            "scripts",
            "repositorio",
            "repo",
            "rodar",
            "executar",
            "criar",
            "listar",
            "abrir",
            "instalar",
            "remover",
            "deletar",
            "apagar",
            "mover",
            "copiar",
            "renomear",
            "editar",
            "salvar",
            "alterar",
            "altere",
            "atualizar",
            "atualize",
            "mudar",
            "mude",
            "run ",
            "execute",
            "create",
            "list ",
            "open ",
            "install",
            "remove",
            "delete",
            "move ",
            "copy ",
            "rename",
            "edit ",
            "save ",
            "change",
            "update",
            "file",
            "files",
            "folder",
            "directory"
        ];

        return actionKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.Ordinal));
    }

    private static string BuildVerificationNotes(CommandExecution execution)
    {
        if (execution.IsCorrect && execution.IsSafe && execution.PassedLocalSafety)
        {
            return "Aprovado pela verificacao do modelo e pela protecao local.";
        }

        var failures = new List<string>();

        if (!execution.IsCorrect)
        {
            failures.Add("o modelo nao confirmou que o comando atende ao objetivo");
        }

        if (!execution.IsSafe)
        {
            failures.Add("o modelo nao considerou o comando seguro");
        }

        if (!execution.PassedLocalSafety)
        {
            failures.Add("a protecao local bloqueou um padrao de comando perigoso");
        }

        return $"Passo bloqueado porque {string.Join("; ", failures)}.";
    }

    private async Task<ConversationMessage> TryAddConversationMessageAsync(
        ConversationMessage message,
        CancellationToken cancellationToken)
    {
        if (conversationMemoryRepository is null)
        {
            return message;
        }

        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationSource.CancelAfter(ConversationMemoryTimeout);
            var savedMessage = await conversationMemoryRepository.AddMessageAsync(message, cancellationSource.Token);
            logger.Log(
                $"Saved {savedMessage.Role} conversation message '{savedMessage.Id}' " +
                $"for ConversationId '{savedMessage.ConversationId}'.");

            return savedMessage;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.Log($"Conversation message persistence for '{message.ConversationId}' was cancelled with the active conversation.");
                return message;
            }

            logger.LogError($"Timed out while persisting conversation message for ConversationId '{message.ConversationId}'.");
            return message;
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to persist conversation message for ConversationId '{message.ConversationId}': {ex.Message}");
            return message;
        }
    }

    private async Task<IReadOnlyList<ConversationMessage>> TryGetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (conversationMemoryRepository is null)
        {
            return [];
        }

        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationSource.CancelAfter(ConversationMemoryTimeout);
            return await conversationMemoryRepository.GetRecentMessagesAsync(conversationId, limit, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.Log($"Conversation history load for '{conversationId}' was cancelled with the active conversation.");
                return [];
            }

            logger.LogError($"Timed out while loading recent messages for ConversationId '{conversationId}'.");
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to load recent messages for ConversationId '{conversationId}': {ex.Message}");
            return [];
        }
    }

    private async Task<ConversationState?> TryGetConversationStateAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationMemoryRepository is null)
        {
            return null;
        }

        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationSource.CancelAfter(ConversationMemoryTimeout);
            return await conversationMemoryRepository.GetStateAsync(conversationId, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.Log($"Conversation state load for '{conversationId}' was cancelled with the active conversation.");
                return null;
            }

            logger.LogError($"Timed out while loading state for ConversationId '{conversationId}'.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to load state for ConversationId '{conversationId}': {ex.Message}");
            return null;
        }
    }

    private async Task TryUpsertConversationStateAsync(ConversationState state, CancellationToken cancellationToken)
    {
        if (conversationMemoryRepository is null)
        {
            return;
        }

        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationSource.CancelAfter(ConversationMemoryTimeout);
            await conversationMemoryRepository.UpsertStateAsync(state, cancellationSource.Token);
            logger.Log($"Saved conversation state for ConversationId '{state.ConversationId}'.");
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.Log($"Conversation state update for '{state.ConversationId}' was cancelled with the active conversation.");
                return;
            }

            logger.LogError($"Timed out while saving state for ConversationId '{state.ConversationId}'.");
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to save state for ConversationId '{state.ConversationId}': {ex.Message}");
        }
    }

    private static ConversationState BuildUpdatedConversationState(
        Guid conversationId,
        ConversationState? previousState,
        string prompt,
        ConversationTurn turn)
    {
        return new ConversationState
        {
            ConversationId = conversationId,
            Summary = BuildUpdatedSummary(previousState?.Summary, prompt, turn.Response),
            CurrentGoal = Truncate(prompt.Trim(), 1000),
            CurrentPlan = BuildCurrentPlan(turn) ?? previousState?.CurrentPlan,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static string BuildUpdatedSummary(string? previousSummary, string prompt, string response)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            builder.AppendLine(previousSummary.Trim());
        }

        builder.AppendLine($"User: {Truncate(prompt.Trim(), 500)}");
        builder.AppendLine($"Assistant: {Truncate(response.Trim(), 500)}");

        return TruncateFromStart(builder.ToString().Trim(), 4000);
    }

    private static string? BuildCurrentPlan(ConversationTurn turn)
    {
        if (turn.Commands.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var command in turn.Commands)
        {
            builder.AppendLine(
                $"{command.Id}. {command.Objective} - {GetCommandStatus(command)}" +
                $"{(command.Required ? " - obrigatorio" : " - opcional")}");
        }

        return Truncate(builder.ToString().Trim(), 2000);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength].Trim();
    }

    private static string TruncateFromStart(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[^maxLength..].Trim();
    }

    private async Task TrySavePromptRequestAsync(PromptRequest request, CancellationToken cancellationToken)
    {
        if (promptRepository is null)
        {
            return;
        }

        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationSource.CancelAfter(PromptPersistenceTimeout);
            await promptRepository.SaveAsync(request, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.Log($"Prompt persistence for '{request.Id}' was cancelled with the active conversation.");
                return;
            }

            logger.LogError($"Timed out while persisting prompt request '{request.Id}'. Continuing with the model response.");
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to persist prompt request '{request.Id}': {ex.Message}");
        }
    }

    private async Task TryUpdatePromptResponseAsync(Guid requestId, string response, CancellationToken cancellationToken)
    {
        if (promptRepository is null)
        {
            return;
        }

        try
        {
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationSource.CancelAfter(PromptPersistenceTimeout);
            await promptRepository.UpdateResponseAsync(requestId, response, cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logger.Log($"Prompt response update for '{requestId}' was cancelled with the active conversation.");
                return;
            }

            logger.LogError($"Timed out while updating prompt response '{requestId}'.");
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to update prompt response '{requestId}': {ex.Message}");
        }
    }

    private async Task<StoredCommand?> TrySaveCommandAsync(Command command)
    {
        if (commandRepository is null)
        {
            return null;
        }

        try
        {
            var storedCommand = new StoredCommand
            {
                RequestId = currentRequestId,
                CommandId = command.Id,
                Objective = command.Objective,
                Command = command.Run,
                OsType = PlatformDetector.GetCurrentOsType()
            };

            return await commandRepository.SaveAsync(storedCommand);
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to persist command '{command.Run}': {ex.Message}");
            return null;
        }
    }

    private async Task TrySaveVerificationAsync(Guid? storedCommandId, CommandExecution execution)
    {
        if (commandRepository is null || storedCommandId is null)
        {
            return;
        }

        try
        {
            await commandRepository.SaveVerificationAsync(new CommandVerification
            {
                CommandId = storedCommandId.Value,
                IsCorrect = execution.IsCorrect,
                IsSafe = execution.IsSafe && execution.PassedLocalSafety,
                VerificationNotes = BuildVerificationNotes(execution)
            });
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to persist verification for command '{storedCommandId}': {ex.Message}");
        }
    }

    private async Task TryUpdateExecutionAsync(Guid? storedCommandId, bool executed, string? result)
    {
        if (commandRepository is null || storedCommandId is null)
        {
            return;
        }

        try
        {
            await commandRepository.UpdateExecutionAsync(storedCommandId.Value, executed, result);
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to update execution for command '{storedCommandId}': {ex.Message}");
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            handler(value);
        }
    }
}
