using System.Text.Json;
using System.Text.Json.Serialization;
using Nebula.Core.Learning;
using Nebula.Core.Operations;
using Nebula.Llama.Client;

namespace Nebula.Agent.Application;

public sealed class ResultValidator(
    ILlamaClient llamaClient,
    IJsonExtractor jsonExtractor,
    ILogger logger) : IResultValidator
{
    private static readonly JsonSerializerOptions ActionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ValidationResult> ValidateAsync(
        string objective,
        AgentToolAction action,
        string observation,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(observation))
        {
            return new ValidationResult(
                ValidationVerdict.Insufficient,
                "Empty observation - cannot validate result.");
        }

        try
        {
            var prompt = BuildValidationPrompt(objective, action, observation, workingDirectory);
            var rawResponse = await llamaClient.GetResponseAsync(prompt, progress: null, cancellationToken);
            var parsed = ModelResponse.Parse(rawResponse);
            var json = jsonExtractor.ExtractJsonObject(parsed.Response);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var verdictText = root.GetProperty("verdict").GetString() ?? "Insufficient";
            var reason = root.GetProperty("reason").GetString() ?? "No reason provided.";

            var verdict = verdictText switch
            {
                "Sufficient" => ValidationVerdict.Sufficient,
                "Mismatch" => ValidationVerdict.Mismatch,
                _ => ValidationVerdict.Insufficient
            };

            AgentToolAction? correctedAction = null;
            if (verdict == ValidationVerdict.Mismatch &&
                root.TryGetProperty("correctedAction", out var actionElement) &&
                actionElement.ValueKind == JsonValueKind.Object)
            {
                correctedAction = JsonSerializer.Deserialize<AgentToolAction>(
                    actionElement.GetRawText(),
                    ActionJsonOptions);
            }

            logger.Log($"[RESULT_VALIDATE] verdict={verdict}; reason={reason}; hasCorrection={correctedAction != null}");

            return new ValidationResult(verdict, reason, correctedAction);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError($"[RESULT_VALIDATE] Failed to validate result: {ex.Message}");
            return new ValidationResult(
                ValidationVerdict.Insufficient,
                $"Validation failed: {ex.Message}");
        }
    }

    private static string BuildValidationPrompt(
        string objective,
        AgentToolAction action,
        string observation,
        string workingDirectory)
    {
        var actionDesc = BuildActionDescription(action);

        return $$"""
            You are Nebula's ResultValidator. Your job is to determine if an action's output PROVES the objective was achieved.

            Objective: {{objective}}

            Action performed:
            {{actionDesc}}

            Observation (command output / result):
            {{observation}}

            Working directory: {{workingDirectory}}

            Analyze carefully:
            1. Does the observation CONFIRM the objective was fully achieved?
            2. If the user asked to create a file - does the observation show the file was created at the correct path?
            3. If the user asked to run a script - does the output show successful execution?
            4. If the user asked to modify a file - does the observation show the modification?
            5. If the output is from a DIFFERENT path than requested, it's a MISMATCH.
            6. If the output shows an error or failure, it's INSUFFICIENT.

            Respond ONLY with valid JSON and no markdown. Format:
            {
              "verdict": "Sufficient" | "Insufficient" | "Mismatch",
              "reason": "Brief explanation in English",
              "correctedAction": {
                "objective": "what the corrected step does",
                "operationKind": "TerminalCommand|FileWrite|FileRead|ScriptContent|ScriptExecution|ProjectScaffold|PlannedPatch",
                "command": "the shell command (for TerminalCommand/ScriptExecution)",
                "content": "file content (for FileWrite/ScriptContent)",
                "targetPath": "path for file operations",
                "templateId": "template id (for ProjectScaffold)",
                "plannedFiles": [{"path": "relative/path", "content": "file content"}] (for PlannedPatch),
                "language": "python|csharp|text",
                "workingDirectory": "",
                "retryJustification": "why this correction addresses the mismatch",
                "requiresSafetyReview": true
              } | null
            }

            CRITICAL: correctedAction MUST be DIFFERENT from the failed action. 
            If verdict is "Sufficient", correctedAction MUST be null.
            If verdict is "Mismatch", correctedAction MUST be provided.
            """;
    }

    private static string BuildActionDescription(AgentToolAction action)
    {
        return action.OperationKind switch
        {
            OperationKind.TerminalCommand => $"TerminalCommand: {action.Command}",
            OperationKind.ScriptExecution => $"ScriptExecution: {action.Command}",
            OperationKind.FileWrite => $"FileWrite: {action.TargetPath} ({action.Language ?? "text"})",
            OperationKind.ScriptContent => $"ScriptContent: {action.TargetPath} ({action.Language ?? "text"})",
            OperationKind.FileRead => $"FileRead: {action.TargetPath}",
            OperationKind.ProjectScaffold => $"ProjectScaffold: {action.TemplateId} at {action.TargetPath}",
            OperationKind.PlannedPatch => $"PlannedPatch: {action.PlannedFiles?.Count ?? 0} files at {action.TargetPath}",
            _ => $"Unknown: {action.Command}"
        };
    }
}