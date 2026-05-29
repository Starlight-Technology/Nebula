using System.Text.RegularExpressions;

namespace Nebula.Llama.Client;

public sealed class ModelResponse
{
    private static readonly Regex ThinkBlockRegex = new(
        "<think>(.*?)</think>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private ModelResponse(string raw, string reasoning, string response)
    {
        Raw = raw;
        Reasoning = reasoning;
        Response = response;
    }

    public string Raw { get; }

    public string Reasoning { get; }

    public string Response { get; }

    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);

    public static ModelResponse Parse(string? raw)
    {
        var content = raw?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(content))
        {
            return new ModelResponse(string.Empty, string.Empty, string.Empty);
        }

        var matches = ThinkBlockRegex.Matches(content);
        var reasoningBlocks = matches
            .Select(match => match.Groups[1].Value.Trim())
            .Where(block => !string.IsNullOrWhiteSpace(block));

        var response = ThinkBlockRegex.Replace(content, string.Empty).Trim();
        var reasoning = string.Join(Environment.NewLine + Environment.NewLine, reasoningBlocks);

        return new ModelResponse(content, reasoning, response);
    }
}
