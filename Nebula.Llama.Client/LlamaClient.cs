using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nebula.Core.Configuration;

namespace Nebula.Llama.Client;

public class LlamaClient : ILlamaClient
{
    private const string DefaultGenerateUrl = "http://localhost:11434/api/generate";
    private const string DefaultModel = "deepseek-r1:7b";
    public const int DefaultLoadTimeoutSeconds = 180;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan DefaultLoadTimeout = TimeSpan.FromSeconds(DefaultLoadTimeoutSeconds);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly TimeSpan requestTimeout;
    private readonly TimeSpan loadTimeout;
    private IReadOnlyList<LlamaModelInfo> cachedModels = [];
    private string selectedModel;
    private bool modelNotFoundFallbackAttempted;

    public LlamaClient(
        HttpClient? httpClient = null,
        string? defaultModel = null,
        string? llamaUrl = null,
        int timeoutSeconds = 0,
        int loadTimeoutSeconds = 0)
    {
        requestTimeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : DefaultRequestTimeout;
        loadTimeout = loadTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(loadTimeoutSeconds)
            : DefaultLoadTimeout;
        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = requestTimeout
        };
        LlamaUrl = llamaUrl
            ?? Environment.GetEnvironmentVariable("LLAMA_URL")
            ?? DefaultGenerateUrl;
        selectedModel = defaultModel
            ?? Environment.GetEnvironmentVariable("LLAMA_MODEL")
            ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL")
            ?? DefaultModel;
    }

    public string LlamaUrl { get; set; }

    public string SelectedModel => selectedModel;

    public async Task<string> GetResponseAsync(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return await GetResponseAsync(prompt, progress: null);
    }

    public async Task<string> GetResponseAsync(
        string prompt,
        IProgress<LlamaStreamUpdate>? progress,
        CancellationToken cancellationToken = default)
    {
        return await GetResponseAsync(
            prompt,
            modelName: null,
            progress,
            cancellationToken);
    }

    public async Task<string> GetResponseAsync(
        string prompt,
        string? modelName,
        IProgress<LlamaStreamUpdate>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return await SendGenerateRequestAsync(
            prompt,
            modelName,
            think: !ShouldDisableThinking(prompt),
            progress: progress,
            cancellationToken: cancellationToken);
    }

    public async Task<string> GetStructuredResponseAsync(
        string prompt,
        object? jsonSchema,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        if (jsonSchema is null)
        {
            return await GetResponseAsync(prompt, progress: null, cancellationToken);
        }

        return await SendGenerateRequestAsync(
            prompt,
            modelName: null,
            think: false,
            format: jsonSchema,
            progress: null,
            cancellationToken: cancellationToken);
    }

    private async Task<string> SendGenerateRequestAsync(
        string prompt,
        string? modelName,
        string? systemPrompt = null,
        bool think = true,
        object? format = null,
        IProgress<LlamaStreamUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendGenerateRequestCoreAsync(prompt, modelName, systemPrompt, think, format, progress, cancellationToken);
        }
        catch (InvalidOperationException ex) when (think && IsThinkingUnsupportedError(ex.Message))
        {
            return await SendGenerateRequestCoreAsync(prompt, modelName, systemPrompt, think: false, format, progress, cancellationToken);
        }
        catch (InvalidOperationException ex) when (string.IsNullOrWhiteSpace(modelName) && IsModelNotFoundError(ex.Message))
        {
            if (modelNotFoundFallbackAttempted)
            {
                throw;
            }

            modelNotFoundFallbackAttempted = true;

            if (await EnsureInstalledModelSelectedAsync(cancellationToken))
            {
                return await SendGenerateRequestCoreAsync(prompt, modelName, systemPrompt, think, format, progress, cancellationToken);
            }

            throw;
        }
    }

    private async Task<string> SendGenerateRequestCoreAsync(
        string prompt,
        string? modelName,
        string? systemPrompt,
        bool think,
        object? format,
        IProgress<LlamaStreamUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var jsonFormat = format
            ?? (ShouldUseJsonFormat(prompt) ? "json" : null);
        var requestModel = string.IsNullOrWhiteSpace(modelName) ? SelectedModel : modelName.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, LlamaUrl)
        {
            Content = JsonContent.Create(new LlamaGenerateRequest
            {
                Model = requestModel,
                Prompt = prompt,
                Stream = true,
                Think = think,
                System = systemPrompt,
                Format = jsonFormat,
                Options = jsonFormat is not null
                    ? new LlamaGenerateOptions
                    {
                        NumPredict = format is null ? 384 : 4096
                    }
                    : null
            })
        };

        await EnsureModelLoadedAsync(requestModel, cancellationToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(BuildRequestTimeoutMessage(requestModel), ex);
        }

        using var responseScope = response;
        await EnsureSuccessAsync(response, requestModel);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var responseText = new StringBuilder();
        var reasoningText = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var chunk = JsonSerializer.Deserialize<LlamaGenerateChunk>(line, JsonOptions);
                if (!string.IsNullOrWhiteSpace(chunk?.Error))
                {
                    throw new InvalidOperationException(chunk.Error);
                }

                if (!string.IsNullOrWhiteSpace(chunk?.Response))
                {
                    responseText.Append(chunk.Response);
                }

                if (!string.IsNullOrWhiteSpace(chunk?.Thinking))
                {
                    reasoningText.Append(chunk.Thinking);
                }

                if (progress is not null && (!string.IsNullOrWhiteSpace(chunk?.Response) || !string.IsNullOrWhiteSpace(chunk?.Thinking)))
                {
                    progress.Report(new LlamaStreamUpdate
                    {
                        Response = responseText.ToString(),
                        Reasoning = reasoningText.ToString()
                    });
                }
            }
            catch (JsonException)
            {
                // Ignora linhas invalidas do stream.
            }
        }

        if (reasoningText.Length == 0)
        {
            return responseText.ToString();
        }

        var reasoning = reasoningText.ToString().Trim();
        var responseContent = responseText.ToString().Trim();

        return string.IsNullOrWhiteSpace(responseContent)
            ? $"<think>{reasoning}</think>"
            : $"<think>{reasoning}</think>{responseContent}";
    }

    public async Task<LlamaRuntimeState> GetRuntimeStateAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var installedModels = await GetInstalledModelsAsync(forceRefresh, cancellationToken);

            return new LlamaRuntimeState
            {
                GenerateUrl = LlamaUrl,
                ApiBaseUrl = BuildApiUrl(string.Empty),
                SelectedModel = SelectedModel,
                IsAvailable = true,
                SelectedModelInstalled = installedModels.Any(model => ModelNamesMatch(model.Name, SelectedModel)),
                InstalledModels = installedModels
            };
        }
        catch (Exception ex)
        {
            return new LlamaRuntimeState
            {
                GenerateUrl = LlamaUrl,
                ApiBaseUrl = BuildApiUrl(string.Empty),
                SelectedModel = SelectedModel,
                IsAvailable = false,
                SelectedModelInstalled = false,
                LastError = ex.Message,
                InstalledModels = []
            };
        }
    }

    public async Task<string?> GetServerVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(BuildApiUrl("version"), cancellationToken);
            await EnsureSuccessAsync(response);

            var payload = await response.Content.ReadFromJsonAsync<LlamaVersionResponse>(JsonOptions, cancellationToken);
            return payload?.Version;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<LlamaModelInfo>> GetInstalledModelsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && cachedModels.Count > 0)
        {
            return cachedModels;
        }

        using var response = await httpClient.GetAsync(BuildApiUrl("tags"), cancellationToken);
        await EnsureSuccessAsync(response);

        var payload = await response.Content.ReadFromJsonAsync<LlamaTagsResponse>(JsonOptions, cancellationToken);
        cachedModels = (payload?.Models ?? [])
            .OrderByDescending(model => model.ModifiedAt)
            .ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return cachedModels;
    }

    public async Task<bool> SelectModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var normalizedModel = modelName.Trim();
        var installedModels = await GetInstalledModelsAsync(false, cancellationToken);
        var selected = installedModels.FirstOrDefault(model => ModelNamesMatch(model.Name, normalizedModel));

        if (selected is null)
        {
            return false;
        }

        selectedModel = selected.Name;
        return true;
    }

    public async Task<bool> EnsureInstalledModelSelectedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var installedModels = await GetInstalledModelsAsync(true, cancellationToken);
            if (installedModels.Count == 0)
            {
                return false;
            }

            if (installedModels.Any(model => ModelNamesMatch(model.Name, SelectedModel)))
            {
                return true;
            }

            selectedModel = installedModels[0].Name;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<LlamaPullResult> PullModelAsync(
        string modelName,
        bool activateAfterInstall = false,
        IProgress<LlamaPullProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var normalizedModel = modelName.Trim();
        var updates = new List<LlamaPullProgress>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl("pull"))
            {
                Content = JsonContent.Create(new LlamaPullRequest
                {
                    Name = normalizedModel,
                    Stream = true
                })
            };

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(response);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var update = JsonSerializer.Deserialize<LlamaPullProgress>(line, JsonOptions);
                    if (update is null)
                    {
                        continue;
                    }

                    updates.Add(update);
                    progress?.Report(update);

                    if (!string.IsNullOrWhiteSpace(update.Error))
                    {
                        return new LlamaPullResult
                        {
                            ModelName = normalizedModel,
                            Success = false,
                            Activated = false,
                            Message = update.Error,
                            Updates = updates
                        };
                    }
                }
                catch (JsonException)
                {
                    // Ignora linhas invalidas do stream.
                }
            }

            await GetInstalledModelsAsync(true, cancellationToken);

            var activated = false;
            if (activateAfterInstall)
            {
                activated = await SelectModelAsync(normalizedModel, cancellationToken);
            }

            return new LlamaPullResult
            {
                ModelName = normalizedModel,
                Success = true,
                Activated = activated,
                Message = activated
                    ? $"Modelo {SelectedModel} instalado e ativado."
                    : $"Modelo {normalizedModel} instalado com sucesso.",
                Updates = updates
            };
        }
        catch (Exception ex)
        {
            return new LlamaPullResult
            {
                ModelName = normalizedModel,
                Success = false,
                Activated = false,
                Message = ex.Message,
                Updates = updates
            };
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string? modelName = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync();
        if (modelName is not null && IsModelLoadFailure(details))
        {
            throw new InvalidOperationException(BuildModelLoadFailureMessage(modelName, details));
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(details)
                ? $"Ollama returned HTTP {(int)response.StatusCode}."
                : $"Ollama returned HTTP {(int)response.StatusCode}: {details}");
    }

    private async Task EnsureModelLoadedAsync(string modelName, CancellationToken cancellationToken)
    {
        if (await IsModelLoadedAsync(modelName, cancellationToken))
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, LlamaUrl)
        {
            Content = JsonContent.Create(new LlamaGenerateRequest
            {
                Model = modelName,
                Prompt = "ok",
                Stream = false,
                Think = false,
                Options = new LlamaGenerateOptions
                {
                    NumPredict = 1
                }
            })
        };

        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loadCts.CancelAfter(loadTimeout);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                loadCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(BuildLoadTimeoutMessage(modelName), ex);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, modelName);
        }
    }

    private async Task<bool> IsModelLoadedAsync(
        string modelName,
        CancellationToken cancellationToken)
    {
        using var psCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        psCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            using var response = await httpClient.GetAsync(BuildApiUrl("ps"), psCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = await response.Content.ReadFromJsonAsync<LlamaTagsResponse>(
                JsonOptions,
                psCts.Token);
            return (payload?.Models ?? [])
                .Any(model => ModelNamesMatch(model.Name, modelName));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    private string BuildLoadTimeoutMessage(string modelName)
    {
        return $"Ollama nao conseguiu carregar o modelo '{modelName}' em " +
               $"{loadTimeout.TotalSeconds:F0}s. O modelo pode ser grande demais para a " +
               "memoria/CPU disponivel ou a inicializacao travou. Use um modelo menor ou " +
               "com menos parametros, libere memoria ou verifique os logs do Ollama.";
    }

    private string BuildRequestTimeoutMessage(string modelName)
    {
        return $"Ollama nao respondeu para o modelo '{modelName}' em " +
               $"{requestTimeout.TotalSeconds:F0}s. O prompt pode ser grande demais para a " +
               "CPU disponivel ou o modelo pode estar carregando. Aumente " +
               "Nebula:LlmTimeoutSeconds ou use um modelo menor.";
    }

    private static string BuildModelLoadFailureMessage(string modelName, string details)
    {
        var trimmed = details.Length > 400 ? details[..400] : details;
        return $"Ollama nao conseguiu carregar o modelo '{modelName}': {trimmed}. " +
               "Use um modelo menor ou libere memoria antes de tentar novamente.";
    }

    private static bool IsModelLoadFailure(string details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return false;
        }

        string[] markers =
        [
            "timed out waiting for llama-server",
            "failed to load model",
            "error loading model",
            "unable to load model",
            "not enough memory",
            "out of memory",
            "insufficient memory",
            "failed to allocate",
            "requires more system memory"
        ];

        return markers.Any(marker =>
            details.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private string BuildApiUrl(string endpoint)
    {
        var baseUri = new Uri(LlamaUrl, UriKind.Absolute);
        var path = baseUri.AbsolutePath;
        var apiIndex = path.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        var rootPath = apiIndex >= 0 ? path[..apiIndex] : path.TrimEnd('/');
        var builder = new UriBuilder(baseUri)
        {
            Path = string.IsNullOrWhiteSpace(endpoint)
                ? $"{rootPath.TrimEnd('/')}/api"
                : $"{rootPath.TrimEnd('/')}/api/{endpoint.TrimStart('/')}",
            Query = string.Empty
        };

        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool ModelNamesMatch(string left, string right)
    {
        return string.Equals(CanonicalizeModelName(left), CanonicalizeModelName(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalizeModelName(string modelName)
    {
        var trimmed = modelName.Trim();
        return trimmed.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^7]
            : trimmed;
    }

    private static bool ShouldDisableThinking(string prompt)
    {
        var normalized = prompt.TrimStart();

        return ShouldUseJsonFormat(prompt)
            || normalized.StartsWith("Response only with \"Yes\" or \"No\".", StringComparison.Ordinal);
    }

    private static bool ShouldUseJsonFormat(string prompt)
    {
        var normalized = prompt.TrimStart();

        return normalized.StartsWith("You are a command planner.", StringComparison.Ordinal)
            || normalized.Contains("Respond ONLY with valid JSON", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Return only JSON", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsThinkingUnsupportedError(string message)
    {
        return message.Contains("does not support thinking", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModelNotFoundError(string message)
    {
        return message.Contains("model not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("model '", StringComparison.OrdinalIgnoreCase) && message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LlamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("system")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? System { get; set; }

        [JsonPropertyName("think")]
        public bool Think { get; set; }

        [JsonPropertyName("format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Format { get; set; }

        [JsonPropertyName("options")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LlamaGenerateOptions? Options { get; set; }
    }

    private sealed class LlamaGenerateOptions
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("num_predict")]
        public int NumPredict { get; set; } = 384;
    }

    private sealed class LlamaGenerateChunk
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("thinking")]
        public string? Thinking { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    private sealed class LlamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<LlamaModelInfo>? Models { get; set; }
    }

    private sealed class LlamaVersionResponse
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    private sealed class LlamaPullRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }
}
