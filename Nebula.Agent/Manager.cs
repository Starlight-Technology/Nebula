//-----------------------------------------------------------------------
// <copyright file="Manager.cs" company="Starlight-Technology">
//     Author:  
//     Copyright (c) Starlight-Technology. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using Nebula.Llama.Client;
using Nebula.Runner;
using Nebula.Agent.Data;

using System.Text.Json;

namespace Nebula.Agent;

public class Manager(
    ILlamaClient llamaClient, 
    IShellExecutor executor, 
    IJsonExtractor jsonExtractor, 
    ILogger logger,
    ICommandRepository? commandRepository = null,
    IPromptRequestRepository? promptRepository = null) : IManager
{
    private string lastPrompt = string.Empty;
    private Guid currentRequestId = Guid.NewGuid();

    private async Task<string> ExtractJsonObjectAsync(string input)
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

    private async Task<string> GetCommandStep(string action)
    {
        try
        {
            lastPrompt = action;

            string commandsStr = await GenerateCommandSteps(action);

            string json = await ExtractJsonObjectAsync(commandsStr);

            CommandSteps? wrapper = JsonSerializer.Deserialize<CommandSteps>(json);

            List<Command> commands = wrapper?.Steps ?? new List<Command>();

            foreach (Command command in commands)
            {
                await VerifyCommand(command);
            }

            return "Commands executed";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting command steps: {ex.Message}");
            return await GetCommandStep(action);
        }
    }

    private async Task<string> HandleChat(string message) 
    { 
        return await llamaClient.GetResponseAsync(message); 
    }

    private async Task VerifyCommand(Command command)
    {
        bool isSafe = await VerifyCommandSafetyAsync(command);
        bool isCorrect = await VerifyCommandCorrectAsync(command);

        if (isSafe && isCorrect)
        {
            // Persist verified command before execution
            if (commandRepository != null)
            {
                var storedCommand = new StoredCommand
                {
                    RequestId = currentRequestId,
                    CommandId = command.Id,
                    Objective = command.Objective,
                    Command = command.Run,
                    OsType = PlatformDetector.GetCurrentOsType()
                };

                var savedCommand = await commandRepository.SaveAsync(storedCommand);

                // Record verification results
                var verification = new CommandVerification
                {
                    CommandId = savedCommand.Id,
                    IsCorrect = isCorrect,
                    IsSafe = isSafe,
                    VerificationNotes = "Command passed correctness and safety verification"
                };

                await commandRepository.SaveVerificationAsync(verification);

                // Execute command and update status
                string result = await executor.RunCommandAsync(command.Run);
                logger.Log(result);

                await commandRepository.UpdateExecutionAsync(savedCommand.Id, true, result);
            }
            else
            {
                // Fallback if no repository available
                string result = await executor.RunCommandAsync(command.Run);
                logger.Log(result);
            }
            return;
        }

        // Record failed verification
        if (commandRepository != null)
        {
            var storedCommand = new StoredCommand
            {
                RequestId = currentRequestId,
                CommandId = command.Id,
                Objective = command.Objective,
                Command = command.Run,
                OsType = PlatformDetector.GetCurrentOsType(),
                Executed = false
            };

            var savedCommand = await commandRepository.SaveAsync(storedCommand);

            var failedVerification = new CommandVerification
            {
                CommandId = savedCommand.Id,
                IsCorrect = isCorrect,
                IsSafe = isSafe,
                VerificationNotes = $"Verification failed - Safe: {isSafe}, Correct: {isCorrect}"
            };

            await commandRepository.SaveVerificationAsync(failedVerification);
        }

        logger.LogError($"Command verification failed for objective: {command.Objective}. Safe: {isSafe}, Correct: {isCorrect}");
    }

    public async Task<bool> VerifyCommandCorrectAsync(Command command)
    {
        string response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". This following command {{command.Run}} execute EXACTLY {{command.Objective}} on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}? Command: {{command.Run}}
            """);

        return response.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> VerifyCommandSafetyAsync(Command command)
    {
        string response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". Is the following command safe to execute on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}? Command: {{command.Run}}
            """);

        return response.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GenerateCommandSteps(string userRequest)
    {
        if (string.IsNullOrWhiteSpace(userRequest))
            throw new ArgumentException("User request cannot be null or empty.", nameof(userRequest));

        string payloadPrompt = $$"""
                        You are a command planner.

                        Your job:
                        - Convert the user request into a sequence of shell commands on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}.
                        - Each command must be a step to be executed on terminal only.
                        - Respond ONLY in valid JSON.
                        - Do NOT add explanations, comments or extra text.

                        Response format:
                        {
                            "Steps": [
                            { "Id": 1 "Objective":"why this command" "Run": "first shell command here" },
                          ]
                        }
                        add more steps if needed, but keep the JSON format.

                        User request:
                        {{userRequest}}
                        """;

        return await llamaClient.GetResponseAsync(payloadPrompt);
    }

    public async Task<string> ManageResponse(string prompt)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return "The prompt are empty, write something.";

            // Create new request context
            currentRequestId = Guid.NewGuid();

            ClassificationResult classification = await llamaClient.ClassifyPrompt(prompt);

            // Persist prompt request
            if (promptRepository != null)
            {
                var promptRequest = new PromptRequest
                {
                    Id = currentRequestId,
                    Prompt = prompt,
                    Classification = classification.ToString()
                };

                await promptRepository.SaveAsync(promptRequest);
            }

            return classification switch
            {
                ClassificationResult.Action => await GetCommandStep(prompt),
                ClassificationResult.Chat => await HandleChat(prompt),
                _ => "Unable to classify the prompt. Please try again with a clearer request."
            };
        }
        catch (Exception ex)
        {
            logger.LogError($"Error managing response: {ex.Message}");
            throw;
        }
    }
}
