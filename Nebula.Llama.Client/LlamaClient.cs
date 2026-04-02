using System.Text;
using System.Text.Json;

namespace Nebula.Llama.Client;

public class Payload
{
    public string Model { get; set; } = "deepseek-r1:7b";
    public string Prompt { get; set; } = string.Empty;
}

public enum ClassificationResult
{
    Action,
    Chat,
    Unknown
}

public class LlamaClient : ILlamaClient
{
    public string LlamaUrl { get; set; } = "http://localhost:11434/api/generate";

    public async Task<ClassificationResult> ClassifyPrompt(string prompt)
    {
        var payload = new Payload
        {
            Prompt = $"You are an intent classifier. \r\n " +
            $"Classify the user message into one of two categories: \r\n " +
            $"action = only when the user wants the computer to perform an operation such as:\r\n- creating files\r\n- listing directories\r\n- running commands\r\n- modifying data\r\n- executing scripts\r\n- interacting with the operating system\r\n\r\n" +
            $"chat = all other cases, including:\r\n- cooking\r\n- advice\r\n- explanations\r\n- real‑world tasks\r\n- questions\r\n- conversation\r\n\r\n " +
            $"Respond ONLY with: action or chat.\r\n" +
            $" No explanations. No extra text.\r\n" +
            $" message: {prompt}"
        };

        var raw = await GetResponseAsync(payload.Prompt);

        // Normaliza a resposta
        var result = raw
            .Trim()
            .ToLowerInvariant();

        return result switch
        {
            "action" => ClassificationResult.Action,
            "chat" => ClassificationResult.Chat,
            _ => await ClassifyPrompt(prompt)
        };

    }

    public async Task<string> GetResponseAsync(string prompt)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, LlamaUrl);

        var payload = new Payload { Prompt = prompt };
        var json = JsonSerializer.Serialize(payload);

        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        Console.WriteLine("Enviando solicitação para Llama...");
        Console.WriteLine($"Prompt: {prompt}");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        string? line;
        var fullText = new StringBuilder();

        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var jsonResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(line);
                if (jsonResponse != null && jsonResponse.TryGetValue("response", out var token))
                {
                    fullText.Append(token.ToString());
                }
            }
            catch
            {
                // ignora linhas inválidas
            }
        }

        Console.WriteLine($"Llama response received: {fullText}");

        return fullText.ToString();
    }
}
