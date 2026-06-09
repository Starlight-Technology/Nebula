using Nebula.Llama.Client;

namespace Nebula.Agent.Application;

internal sealed class PromptClassifier(ILlamaClient llamaClient, ILogger logger)
{
    public async Task<ClassificationResult> ClassifyAsync(string prompt)
    {
        if (!ComputerOperationDetector.IsOperational(prompt))
        {
            logger.Log($"Prompt '{prompt}' was classified locally as chat before calling the model classifier.");
            return ClassificationResult.Chat;
        }

        var classification = await llamaClient.ClassifyPrompt(prompt);
        if (classification != ClassificationResult.Unknown)
        {
            return classification;
        }

        logger.Log($"Prompt '{prompt}' received an unknown model classification and was mapped locally to Action.");
        return ClassificationResult.Action;
    }
}
