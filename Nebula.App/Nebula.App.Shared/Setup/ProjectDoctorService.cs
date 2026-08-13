using System.Net.Sockets;

using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.App.Shared.Setup;

public interface IProjectDoctorService
{
    Task<ProjectDiagnosticReport> RunAsync(CancellationToken cancellationToken = default);
}

public sealed class ProjectDiagnosticItem
{
    public string Name { get; init; } = string.Empty;

    public bool Available { get; init; }

    public string Detail { get; init; } = string.Empty;

    public string? Suggestion { get; init; }
}

public sealed class ProjectDiagnosticReport
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<ProjectDiagnosticItem> Items { get; init; } = [];

    public bool AllHealthy => Items.Count > 0 && Items.All(item => item.Available);

    public int HealthyCount => Items.Count(item => item.Available);

    public int ProblemCount => Items.Count - HealthyCount;
}

public sealed class ProjectDoctorService(
    IShellExecutor shellExecutor,
    ILlamaClient llamaClient) : IProjectDoctorService
{
    public async Task<ProjectDiagnosticReport> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<ProjectDiagnosticItem>();

        items.Add(await ProbeAsync(
            "SDK .NET",
            "dotnet --version",
            "Instale o SDK .NET 10+ em https://dotnet.microsoft.com/download.",
            cancellationToken));

        items.Add(await ProbeAsync(
            "Python",
            "python --version",
            "Instale Python 3.10+ ou ajuste o resolvedor de scripts do agente.",
            cancellationToken));

        items.Add(await ProbeAsync(
            "Git",
            "git --version",
            "Instale o Git em https://git-scm.com/downloads.",
            cancellationToken));

        items.Add(await ProbeGitRepositoryAsync(cancellationToken));

        items.Add(await ProbeAsync(
            "Docker",
            "docker --version",
            "Inicie o Docker Desktop ou o daemon do Docker antes dos containers.",
            cancellationToken));

        items.Add(await ProbeDockerComposeAsync(cancellationToken));

        items.Add(await ProbeOllamaAsync(cancellationToken));

        items.Add(await ProbePortAsync(
            "PostgreSQL (porta 5432)",
            5432,
            "Rode `docker compose up -d postgres` e confira POSTGRES_CONNECTION.",
            cancellationToken));

        items.Add(await ProbePortAsync(
            "MongoDB (porta 27017)",
            27017,
            "Rode `docker compose up -d mongodb` e confira MONGO_CONNECTION.",
            cancellationToken));

        items.Add(await ProbePortAsync(
            "SearXNG (porta 8080)",
            8080,
            "Rode `docker compose up -d searxng` para habilitar pesquisa web local.",
            cancellationToken));

        return new ProjectDiagnosticReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Items = items
        };
    }

    private async Task<ProjectDiagnosticItem> ProbeAsync(
        string name,
        string command,
        string suggestion,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = SecretRedaction.Apply(
                (await shellExecutor.RunCommandAsync(command, cancellationToken))
                .Trim()) ?? string.Empty;
            return new ProjectDiagnosticItem
            {
                Name = name,
                Available = !string.IsNullOrWhiteSpace(output),
                Detail = FirstLine(output),
                Suggestion = string.IsNullOrWhiteSpace(output) ? suggestion : null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProjectDiagnosticItem
            {
                Name = name,
                Available = false,
                Detail = ex.Message,
                Suggestion = suggestion
            };
        }
    }

    private async Task<ProjectDiagnosticItem> ProbeDockerComposeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var output = SecretRedaction.Apply(
                (await shellExecutor.RunCommandAsync(
                    "docker compose config --quiet",
                    cancellationToken))
                .Trim());
            return new ProjectDiagnosticItem
            {
                Name = "Docker Compose (arquivo do projeto)",
                Available = string.IsNullOrWhiteSpace(output) || output.Contains("error", StringComparison.OrdinalIgnoreCase) is false,
                Detail = string.IsNullOrWhiteSpace(output)
                    ? "compose config valido"
                    : FirstLine(output),
                Suggestion = string.IsNullOrWhiteSpace(output) ? null : "Revise docker-compose.yml na raiz do projeto."
            };
        }
        catch (Exception ex)
        {
            return new ProjectDiagnosticItem
            {
                Name = "Docker Compose (arquivo do projeto)",
                Available = false,
                Detail = ex.Message,
                Suggestion = "Revise docker-compose.yml na raiz do projeto."
            };
        }
    }

    private async Task<ProjectDiagnosticItem> ProbeGitRepositoryAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var output = SecretRedaction.Apply(
                (await shellExecutor.RunCommandAsync(
                    "git rev-parse --is-inside-work-tree",
                    cancellationToken))
                .Trim()) ?? string.Empty;
            var isRepo = output.Equals("true", StringComparison.OrdinalIgnoreCase);
            return new ProjectDiagnosticItem
            {
                Name = "Repositorio git do workspace",
                Available = isRepo,
                Detail = isRepo
                    ? "diretorio e um repositorio git"
                    : output.Length > 120 ? output[..120] : output,
                Suggestion = isRepo
                    ? null
                    : "Rode `git init` na raiz do workspace para habilitar controle de versao."
            };
        }
        catch (Exception ex)
        {
            return new ProjectDiagnosticItem
            {
                Name = "Repositorio git do workspace",
                Available = false,
                Detail = ex.Message,
                Suggestion = "Rode `git init` na raiz do workspace para habilitar controle de versao."
            };
        }
    }

    private async Task<ProjectDiagnosticItem> ProbeOllamaAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var version = await llamaClient.GetServerVersionAsync(cancellationToken);
            return new ProjectDiagnosticItem
            {
                Name = "Ollama runtime",
                Available = !string.IsNullOrWhiteSpace(version),
                Detail = version is null
                    ? "Sem resposta no endpoint configurado."
                    : $"v{version} em {llamaClient.LlamaUrl}",
                Suggestion = version is null
                    ? "Inicie o Ollama ou rode `docker compose up -d ollama`."
                    : null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProjectDiagnosticItem
            {
                Name = "Ollama runtime",
                Available = false,
                Detail = ex.Message,
                Suggestion = "Inicie o Ollama ou rode `docker compose up -d ollama`."
            };
        }
    }

    private static async Task<ProjectDiagnosticItem> ProbePortAsync(
        string name,
        int port,
        string suggestion,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsBrowser())
        {
            return new ProjectDiagnosticItem
            {
                Name = name,
                Available = false,
                Detail = "indisponivel no navegador",
                Suggestion = "Rode o diagnostico a partir do app web servido ou do shell nativo."
            };
        }

        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            var connectTask = client.ConnectAsync(
                "localhost",
                port,
                timeoutCts.Token);
            await connectTask;

            return new ProjectDiagnosticItem
            {
                Name = name,
                Available = true,
                Detail = "porta aberta"
            };
        }
        catch (OperationCanceledException)
        {
            return new ProjectDiagnosticItem
            {
                Name = name,
                Available = false,
                Detail = "porta fechada",
                Suggestion = suggestion
            };
        }
        catch (Exception)
        {
            return new ProjectDiagnosticItem
            {
                Name = name,
                Available = false,
                Detail = "porta fechada",
                Suggestion = suggestion
            };
        }
    }

    private static string FirstLine(string text)
    {
        var line = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim() ?? string.Empty;
        return line.Length > 120 ? line[..120] : line;
    }
}
