namespace Nebula.Llama.Client;

public interface ILlamaClient
{
    string LlamaUrl { get; set; }

    Task<ClassificationResult> ClassifyPrompt(string prompt);

    Task<string> GetResponseAsync(string prompt);
}