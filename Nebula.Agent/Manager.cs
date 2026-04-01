//-----------------------------------------------------------------------
// <copyright file="Manager.cs" company="Starlight-Technology">
//     Author:  
//     Copyright (c) Starlight-Technology. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using Nebula.Llama.Client;
using Nebula.Runner;

using System.Text.Json;

namespace Nebula.Agent;

public class Manager(ILlamaClient llamaClient, IShellExecutor executor, IJsonExtractor jsonExtractor, ILogger logger) : IManager
{
    private string lastPrompt = string.Empty;

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

    private async Task<string> HandleChat(string message) 
    { 
        return await llamaClient.GetResponseAsync(message); 
    }

    private async Task VerifyCommand(Command command)
    {
        if ((await VerifyCommandSafetyAsync(command)) && (await VerifyCommandCorrectAsync(command)))
        {
            string result = await executor.RunCommandAsync(command.Run);
            logger.Log(result);
            return;
        }

        await GetCommandStep(command.Objective);
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
                        - Each command must be a step.
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

            ClassificationResult classification = await llamaClient.ClassifyPrompt(prompt);

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
            logger.LogError($"Retrying prompt: {prompt}");
            return await ManageResponse(prompt);
        }
    }
}
