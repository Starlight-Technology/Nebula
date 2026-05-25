namespace Nebula.Llama.Client;

public interface ILlamaClient
{
    string LlamaUrl { get; set; }

    string SelectedModel { get; }

    Task<ClassificationResult> ClassifyPrompt(string prompt);

    Task<string> GetResponseAsync(string prompt);

    Task<string> GetResponseAsync(
        string prompt,
        IProgress<LlamaStreamUpdate>? progress,
        CancellationToken cancellationToken = default);

    Task<LlamaRuntimeState> GetRuntimeStateAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlamaModelInfo>> GetInstalledModelsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    Task<bool> SelectModelAsync(string modelName, CancellationToken cancellationToken = default);

    Task<LlamaPullResult> PullModelAsync(
        string modelName,
        bool activateAfterInstall = false,
        IProgress<LlamaPullProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
