using Nebula.Core.Learning;
using Nebula.Llama.Client;

namespace Nebula.Agent.Application;

public sealed class TranslationService : ITranslationService
{
    private const string TranslationPromptTemplate =
        "Translate the following text to {0}. Return ONLY the translated text, " +
        "with no explanations, no quotes, no prefixes, no suffixes.\n\n{1}";

    private readonly ILlamaClient llamaClient;
    private readonly ILogger logger;

    public TranslationService(ILlamaClient llamaClient, ILogger logger)
    {
        this.llamaClient = llamaClient;
        this.logger = logger;
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var prompt = string.Format(
            TranslationPromptTemplate,
            targetLanguage,
            text);

        try
        {
            var response = await llamaClient.GetResponseAsync(
                prompt,
                progress: null,
                cancellationToken);

            var translated = response?.Trim().Trim('"', '\'', '`', '\n', '\r') ?? string.Empty;

            if (string.IsNullOrWhiteSpace(translated))
            {
                logger.Log("[TRANSLATION] LLM returned empty translation; falling back to original.");
                return text;
            }

            logger.Log(
                $"[TRANSLATION] {sourceLanguage ?? "auto"} -> {targetLanguage}: " +
                $"\"{text.Truncate(80)}\" -> \"{translated.Truncate(80)}\"");
            return translated;
        }
        catch (Exception ex)
        {
            logger.Log($"[TRANSLATION] Failed: {ex.Message}; falling back to original.");
            return text;
        }
    }
}

file static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength] + "...";
    }
}
