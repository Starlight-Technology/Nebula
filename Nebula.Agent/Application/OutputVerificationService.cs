using System.Text.Json;

using Nebula.Core.Commands;
using Nebula.Core.Learning;
using Nebula.Llama.Client;

namespace Nebula.Agent.Application;

public sealed class OutputVerificationService(
    ILlamaClient llamaClient,
    IJsonExtractor jsonExtractor,
    ICommandIntentParser commandIntentParser,
    ICommandResolver commandResolver,
    IRuntimeCommandEnvironmentDetector environmentDetector,
    ILogger logger)
    : IOutputVerificationService
{
    public async Task<OutputVerification> VerifyAsync(
        string objective,
        string command,
        string output,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new OutputVerification(
                OutputVerdict.Uncertain,
                "Empty output - cannot verify.");
        }

        var trimmedOutput = output.Trim();
        if (trimmedOutput.Length > 3000)
        {
            trimmedOutput = trimmedOutput[..3000] + "...";
        }

        try
        {
            var prompt = $$"""
                You are an output verifier. Your job is to check if the command output matches what the user actually asked for.

                User objective: {{objective}}
                Command executed: {{command}}
                Working directory: {{workingDirectory}}
                Command output:
                {{trimmedOutput}}

                Analyze carefully:
                1. Does the command match the user's objective?
                2. Does the output show the correct location/path requested?
                3. If the user asked for a specific drive or directory, does the output refer to that drive/directory?
                4. If the output is from a different path than requested, it's a MISMATCH.

                Respond ONLY with valid JSON and no markdown. Format:
                {
                  "verdict": "Match" | "Mismatch" | "Uncertain",
                  "reason": "Brief explanation in the same language as the objective",
                  "correctedCommand": "A corrected command that would produce the right output, or empty if match"
                }
                """;

            var rawResponse = await llamaClient.GetResponseAsync(
                prompt,
                progress: null,
                cancellationToken);

            var parsed = ModelResponse.Parse(rawResponse);
            var json = jsonExtractor.ExtractJsonObject(parsed.Response);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var verdictText = root.GetProperty("verdict").GetString() ?? "Uncertain";
            var reason = root.GetProperty("reason").GetString() ?? "No reason provided.";
            var correctedCommand = root.TryGetProperty("correctedCommand", out var cc)
                ? cc.GetString()
                : null;

            var verdict = verdictText switch
            {
                "Match" => OutputVerdict.Match,
                "Mismatch" => OutputVerdict.Mismatch,
                _ => OutputVerdict.Uncertain
            };

            if (!string.IsNullOrWhiteSpace(correctedCommand) &&
                verdict == OutputVerdict.Mismatch)
            {
                var resolved = TryResolveCommand(correctedCommand, workingDirectory);
                if (resolved is not null)
                {
                    correctedCommand = resolved;
                }
            }

            logger.Log(
                $"[OUTPUT_VERIFY] objective={objective}; command={command}; " +
                $"verdict={verdict}; reason={reason}; " +
                $"correctedCommand={correctedCommand ?? "(none)"}");

            return new OutputVerification(verdict, reason, correctedCommand);
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"[OUTPUT_VERIFY] Failed to verify output: {ex.Message}");
            return new OutputVerification(
                OutputVerdict.Uncertain,
                $"Verification failed: {ex.Message}");
        }
    }

    private string? TryResolveCommand(
        string correctedCommand,
        string workingDirectory)
    {
        try
        {
            var environment = environmentDetector.Detect(workingDirectory);
            var request = commandIntentParser.Parse(
                correctedCommand,
                correctedCommand,
                workingDirectory);
            var resolved = commandResolver.Resolve(request, environment);
            return resolved.DisplayCommand;
        }
        catch
        {
            return correctedCommand;
        }
    }
}
