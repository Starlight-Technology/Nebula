using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nebula.Llama.Client;

public class LlamaClient : ILlamaClient
{
    private const string DefaultGenerateUrl = "http://localhost:11434/api/generate";
    private const string DefaultModel = "deepseek-r1:7b";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private IReadOnlyList<LlamaModelInfo> cachedModels = [];
    private string selectedModel;

    public LlamaClient(HttpClient? httpClient = null, string? defaultModel = null, string? llamaUrl = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
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
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return await SendGenerateRequestAsync(
            prompt,
            think: !ShouldDisableThinking(prompt),
            progress: progress,
            cancellationToken: cancellationToken);
    }

    private async Task<string> SendGenerateRequestAsync(
        string prompt,
        string? systemPrompt = null,
        bool think = true,
        IProgress<LlamaStreamUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendGenerateRequestCoreAsync(prompt, systemPrompt, think, progress, cancellationToken);
        }
        catch (InvalidOperationException ex) when (think && IsThinkingUnsupportedError(ex.Message))
        {
            return await SendGenerateRequestCoreAsync(prompt, systemPrompt, think: false, progress, cancellationToken);
        }
    }

    private async Task<string> SendGenerateRequestCoreAsync(
        string prompt,
        string? systemPrompt,
        bool think,
        IProgress<LlamaStreamUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        using var request = new HttpRequestMessage(HttpMethod.Post, LlamaUrl)
        {
            Content = JsonContent.Create(new LlamaGenerateRequest
            {
                Model = SelectedModel,
                Prompt = prompt,
                Stream = true,
                Think = think,
                System = systemPrompt
            })
        };

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response);

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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(details)
                ? $"Ollama returned HTTP {(int)response.StatusCode}."
                : $"Ollama returned HTTP {(int)response.StatusCode}: {details}");
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

        return normalized.StartsWith("You are a command planner.", StringComparison.Ordinal)
            || normalized.StartsWith("Response only with \"Yes\" or \"No\".", StringComparison.Ordinal);
    }

    private static bool IsThinkingUnsupportedError(string message)
    {
        return message.Contains("does not support thinking", StringComparison.OrdinalIgnoreCase);
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

    private sealed class LlamaPullRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }
}
