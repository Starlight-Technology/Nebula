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

public class Manager(ILlamaClient llamaClient, IShellExecutor executor) : IManager
{
    string lastPrompt = string.Empty;

    async Task<string> ExtractJsonObject(string input)
    {
        try
        {
            int start = input.IndexOf('{');
            int end = input.LastIndexOf('}');

            if((start < 0) || (end < 0) || (end <= start))
                throw new Exception("No valid JSON object found.");

            return input.Substring(start, (end - start) + 1);
        } catch(Exception ex)
        {
            Console.WriteLine($"Error extracting JSON: {ex.Message}");

            return await GetCommandStep(lastPrompt);
        }

    }

    async Task<string> GetCommandStep(string action)
    {
        lastPrompt = action;

        string commandsStr = await GenerateCommandSteps(action);

        string json = await ExtractJsonObject(commandsStr);

        CommandSteps? wrapper = JsonSerializer.Deserialize<CommandSteps>(json);

        List<Command> commands = wrapper?.Steps ?? new List<Command>();

        foreach(Command command in commands)
        {
            await VerifyCommand(command);
        }

        return "Commands executed";
    }

    async Task<string> HandleChat(string message) { return await llamaClient.GetResponseAsync(message); }

    async Task VerifyCommand(Command command)
    {
        if((await VerifyCommandSafetyAsync(command)) && (await VerifyCommandCorrectAsync(command)))
        {
            string result = await executor.RunCommandAsync(command.Run);
            Console.WriteLine(result);
            return;
        }

        await GetCommandStep(command.Objective);
    }

    async Task<bool> VerifyCommandCorrectAsync(Command command)
    {
        string response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". This following command {{command.Run}} execute {{command.Objective}} on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}? Command: {{command.Run}}
            """);

        return response.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase);
    }

    async Task<bool> VerifyCommandSafetyAsync(Command command)
    {
        string response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". Is the following command safe to execute on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}? Command: {{command.Run}}
            """);

        return response.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GenerateCommandSteps(string userRequest)
    {
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

    public Task<string> ManageResponse(string prompt)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(prompt))
                return Task.FromResult("The prompt are empty, write something.");
            ClassificationResult classification = llamaClient.ClassifyPrompt(prompt).GetAwaiter().GetResult();
            return classification switch
            {
                ClassificationResult.Action => GetCommandStep(prompt),
                ClassificationResult.Chat => HandleChat(prompt),
                _ => Task.FromResult($"Unknown classification for prompt: {prompt}")
            };
        } catch(Exception ex)
        {
            return Task.FromResult($"Error managing response: {ex.Message}");
        }
    }
}
