namespace Nebula.Llama.Client;

public sealed class LlamaStreamUpdate
{
    public string Response { get; init; } = string.Empty;

    public string Reasoning { get; init; } = string.Empty;
}
