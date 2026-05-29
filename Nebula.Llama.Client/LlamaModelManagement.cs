using System.Text.Json.Serialization;

namespace Nebula.Llama.Client;

public sealed class LlamaModelInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("modified_at")]
    public DateTimeOffset? ModifiedAt { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("details")]
    public LlamaModelDetails? Details { get; set; }

    [JsonIgnore]
    public string SizeLabel => FormatBytes(SizeBytes);

    [JsonIgnore]
    public string ModifiedAtLabel => ModifiedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "desconhecida";

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(Details?.ParameterSize))
            {
                parts.Add(Details.ParameterSize);
            }

            if (!string.IsNullOrWhiteSpace(Details?.Family))
            {
                parts.Add(Details.Family);
            }

            if (!string.IsNullOrWhiteSpace(Details?.QuantizationLevel))
            {
                parts.Add(Details.QuantizationLevel);
            }

            if (!string.IsNullOrWhiteSpace(Details?.Format))
            {
                parts.Add(Details.Format);
            }

            return parts.Count > 0
                ? string.Join(" | ", parts)
                : "Detalhes indisponiveis";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "tamanho desconhecido";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#}{units[unitIndex]}";
    }
}

public sealed class LlamaModelDetails
{
    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }

    [JsonPropertyName("quantization_level")]
    public string? QuantizationLevel { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }
}

public sealed class LlamaPullProgress
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("completed")]
    public long? Completed { get; set; }

    [JsonPropertyName("total")]
    public long? Total { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonIgnore]
    public int? PercentComplete
    {
        get
        {
            if (Completed is null || Total is null || Total <= 0)
            {
                return null;
            }

            return (int)Math.Clamp(Math.Round((double)Completed.Value / Total.Value * 100), 0, 100);
        }
    }

    [JsonIgnore]
    public string Summary => PercentComplete is int percent
        ? $"{Status} ({percent}%)"
        : Status;
}

public sealed class LlamaPullResult
{
    public string ModelName { get; init; } = string.Empty;

    public bool Success { get; init; }

    public bool Activated { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<LlamaPullProgress> Updates { get; init; } = [];
}

public sealed class LlamaRuntimeState
{
    public string GenerateUrl { get; init; } = string.Empty;

    public string ApiBaseUrl { get; init; } = string.Empty;

    public string SelectedModel { get; init; } = string.Empty;

    public bool IsAvailable { get; init; }

    public bool SelectedModelInstalled { get; init; }

    public string? LastError { get; init; }

    public IReadOnlyList<LlamaModelInfo> InstalledModels { get; init; } = [];
}
