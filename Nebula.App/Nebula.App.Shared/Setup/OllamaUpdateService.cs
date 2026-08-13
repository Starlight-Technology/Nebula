using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.App.Shared.Setup;

public interface IOllamaUpdateService
{
    Task<string?> GetServerVersionAsync(CancellationToken cancellationToken = default);

    Task<OllamaUpdateResult> UpdateServerAsync(CancellationToken cancellationToken = default);
}

public sealed class OllamaUpdateResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? PreviousVersion { get; init; }

    public string? NewVersion { get; init; }

    public IReadOnlyList<string> OutputLines { get; init; } = [];
}

public sealed class OllamaUpdateService(
    ILlamaClient llamaClient,
    IShellExecutor shellExecutor) : IOllamaUpdateService
{
    public Task<string?> GetServerVersionAsync(CancellationToken cancellationToken = default)
    {
        return llamaClient.GetServerVersionAsync(cancellationToken);
    }

    public async Task<OllamaUpdateResult> UpdateServerAsync(
        CancellationToken cancellationToken = default)
    {
        var previousVersion = await llamaClient.GetServerVersionAsync(cancellationToken);
        var output = new List<string>();

        try
        {
            output.Add("docker compose pull ollama");
            var pullOutput = await shellExecutor.RunCommandAsync(
                "docker compose pull ollama",
                cancellationToken);
            output.Add(TrimOutput(pullOutput));

            output.Add("docker compose up -d ollama");
            var upOutput = await shellExecutor.RunCommandAsync(
                "docker compose up -d ollama",
                cancellationToken);
            output.Add(TrimOutput(upOutput));

            string? newVersion = null;
            for (var attempt = 0; attempt < 10 && newVersion is null; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                newVersion = await llamaClient.GetServerVersionAsync(cancellationToken);
            }

            return new OllamaUpdateResult
            {
                Success = true,
                PreviousVersion = previousVersion,
                NewVersion = newVersion,
                Message = BuildMessage(previousVersion, newVersion),
                OutputLines = output
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            output.Add(ex.Message);
            return new OllamaUpdateResult
            {
                Success = false,
                PreviousVersion = previousVersion,
                Message = $"Nao consegui atualizar o Ollama: {ex.Message}",
                OutputLines = output
            };
        }
    }

    private static string BuildMessage(string? previous, string? next)
    {
        if (next is null)
        {
            return "O Ollama foi atualizado, mas o runtime ainda nao respondeu. Verifique o container.";
        }

        if (previous is null || previous.Equals(next, StringComparison.OrdinalIgnoreCase))
        {
            return $"Ollama ativo na versao {next}. Nenhuma atualizacao pendente.";
        }

        return $"Ollama atualizado de {previous} para {next}.";
    }

    private static string TrimOutput(string output)
    {
        var trimmed = output?.Trim() ?? string.Empty;
        return trimmed.Length > 2000 ? trimmed[^2000..] : trimmed;
    }
}
