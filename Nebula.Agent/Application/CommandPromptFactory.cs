using Nebula.Agent.Data;

namespace Nebula.Agent.Application;

internal static class CommandPromptFactory
{
    public static string CreatePlanPrompt(string userRequest, string conversationContext)
    {
        return $$"""
            You are a command planner.

            Your job:
            - Convert the user request into a sequence of shell commands on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}.
            - Each command must be a step to be executed on terminal only.
            - Use the conversation context to resolve references to previous messages.
            - Mark Required as true unless a step is optional and later steps do not depend on it.
            - Observe the real result of each command before choosing the next one.
            - Never repeat a failed command unless the environment changed or the retry is explicitly justified.
            - After a failure, diagnose the cause and revise the plan with a different action.
            - Stop and request human intervention after the same error occurs 3 times.
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
    }
}
