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
    IPromptRequestRepository? promptRepository = null) : IManager
{
    private static readonly TimeSpan PromptPersistenceTimeout = TimeSpan.FromMilliseconds(800);

    private Guid currentRequestId = Guid.NewGuid();

    public async Task<string> ManageResponse(string prompt)
    {
        var turn = await ManageConversationAsync(prompt);
        return turn.Response;
    }

    public Task<ConversationTurn> ManageConversationAsync(string prompt)
    {
        return ManageConversationAsync(prompt, progress: null, cancellationToken: default);
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
                    RequestId = Guid.Empty,
                    Prompt = prompt,
                    ModelName = llamaClient.SelectedModel,
                    Classification = ClassificationResult.Unknown.ToString(),
                    Response = "The prompt are empty, write something."
                };
            }

            currentRequestId = Guid.NewGuid();

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
                ClassificationResult.Action => await HandleActionAsync(prompt, cancellationToken),
                ClassificationResult.Chat => await HandleChatAsync(prompt, progress, cancellationToken),
                _ => new ConversationTurn
                {
                    RequestId = currentRequestId,
                    Prompt = prompt,
                    ModelName = llamaClient.SelectedModel,
                    Classification = ClassificationResult.Unknown.ToString(),
                    Response = "Unable to classify the prompt. Please try again with a clearer request."
                }
            };

            promptRequest.Response = turn.Response;
            promptRequest.UpdatedAt = DateTime.UtcNow;

            await TryUpdatePromptResponseAsync(currentRequestId, turn.Response, cancellationToken);

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
        if (string.IsNullOrWhiteSpace(userRequest))
        {
            throw new ArgumentException("User request cannot be null or empty.", nameof(userRequest));
        }

        var payloadPrompt = $$"""
                        You are a command planner.

                        Your job:
                        - Convert the user request into a sequence of shell commands on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}.
                        - Each command must be a step to be executed on terminal only.
                        - Respond ONLY in valid JSON.
                        - Do NOT add explanations, comments or extra text.

                        Response format:
                        {
                            "Steps": [
                                { "Id": 1, "Objective": "why this command", "Run": "first shell command here" }
                            ]
                        }
                        Add more steps if needed, but keep the JSON format.

                        User request:
                        {{userRequest}}
                        """;

        return await llamaClient.GetResponseAsync(payloadPrompt);
    }

    private async Task<ConversationTurn> HandleChatAsync(
        string prompt,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken)
    {
        var streamingProgress = progress is null
            ? null
            : new InlineProgress<LlamaStreamUpdate>(update =>
            {
                progress.Report(new ConversationTurn
                {
                    RequestId = currentRequestId,
                    Prompt = prompt,
                    ModelName = llamaClient.SelectedModel,
                    Classification = ClassificationResult.Chat.ToString(),
                    Response = update.Response,
                    Reasoning = string.IsNullOrWhiteSpace(update.Reasoning) ? null : update.Reasoning
                });
            });

        var rawResponse = progress is null
            ? await llamaClient.GetResponseAsync(prompt)
            : await llamaClient.GetResponseAsync(prompt, streamingProgress, cancellationToken);
        var parsedResponse = ModelResponse.Parse(rawResponse);

        return new ConversationTurn
        {
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

    private async Task<ConversationTurn> HandleActionAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            var commandsResponse = await GenerateCommandSteps(prompt, cancellationToken);
            var parsedPlan = ModelResponse.Parse(commandsResponse);
            var responsePayload = string.IsNullOrWhiteSpace(parsedPlan.Response)
                ? commandsResponse
                : parsedPlan.Response;

            var json = ExtractJsonObject(responsePayload);
            var wrapper = JsonSerializer.Deserialize<CommandSteps>(json);
            var plannedCommands = wrapper?.Steps ?? [];
            var executedCommands = new List<CommandExecution>();

            foreach (var command in plannedCommands)
            {
                executedCommands.Add(await ExecuteCommandAsync(command));
            }

            return new ConversationTurn
            {
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

            var fallback = await HandleChatAsync(prompt, progress: null, cancellationToken);
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
            Run = command.Run
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
            builder.AppendLine($"   Corretude: {(command.IsCorrect ? "sim" : "nao")}");
            builder.AppendLine($"   Seguranca do modelo: {(command.IsSafe ? "sim" : "nao")}");
            builder.AppendLine($"   Seguranca local: {(command.PassedLocalSafety ? "sim" : "nao")}");
            builder.AppendLine($"   Status: {(command.Executed ? "executado" : "bloqueado")}");

            if (!string.IsNullOrWhiteSpace(command.Notes))
            {
                builder.AppendLine($"   Observacao: {command.Notes}");
            }
        }

        return builder.ToString().Trim();
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
