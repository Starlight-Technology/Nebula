using Nebula.App.Shared.Setup;
using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.App.Test;

public sealed class ProjectDoctorServiceTest
{
    [Fact]
    public async Task run_must_probe_tools_and_runtime()
    {
        var shell = new FakeDoctorShell();
        var client = new FakeDoctorClient { Version = "0.32.5" };
        var service = new ProjectDoctorService(shell, client);

        var report = await service.RunAsync();

        Assert.True(report.Items.Count >= 10);
        Assert.Contains(report.Items, item => item.Name == "SDK .NET" && item.Available);
        Assert.Contains(report.Items, item => item.Name == "Python" && item.Available);
        Assert.Contains(report.Items, item => item.Name == "Git" && item.Available);
        Assert.Contains(report.Items, item => item.Name == "Docker" && item.Available);
        Assert.Contains(
            report.Items,
            item => item.Name == "Repositorio git do workspace" && item.Available);
        Assert.Contains(
            report.Items,
            item => item.Name == "Ollama runtime" && item.Detail.Contains("0.32.5"));
        Assert.Equal(report.HealthyCount + report.ProblemCount, report.Items.Count);
    }

    [Fact]
    public async Task run_must_report_missing_tool_with_suggestion()
    {
        var shell = new FakeDoctorShell { ThrowOn = "python" };
        var client = new FakeDoctorClient { Version = null };
        var service = new ProjectDoctorService(shell, client);

        var report = await service.RunAsync();

        var python = Assert.Single(report.Items, item => item.Name == "Python");
        Assert.False(python.Available);
        Assert.NotNull(python.Suggestion);

        var ollama = Assert.Single(report.Items, item => item.Name == "Ollama runtime");
        Assert.False(ollama.Available);
        Assert.NotNull(ollama.Suggestion);
    }

    [Fact]
    public async Task run_must_hide_secrets_from_tool_output()
    {
        var shell = new FakeDoctorShell
        {
            VersionOutput = "dotnet 10.0.301 (token=sk-abcdefghijklmnopqrstuvwxyz123456)"
        };
        var client = new FakeDoctorClient { Version = "0.32.5" };
        var service = new ProjectDoctorService(shell, client);

        var report = await service.RunAsync();

        var dotnet = Assert.Single(report.Items, item => item.Name == "SDK .NET");
        Assert.DoesNotContain("sk-", dotnet.Detail);
        Assert.Contains("***", dotnet.Detail);
    }

    private sealed class FakeDoctorShell : IShellExecutor
    {
        public string? ThrowOn { get; set; }

        public string VersionOutput { get; set; } = "dotnet 10.0.301";

        public Task<string> RunCommandAsync(string command)
        {
            return RunCommandAsync(command, CancellationToken.None);
        }

        public Task<string> RunCommandAsync(string command, CancellationToken cancellationToken)
        {
            if (ThrowOn is not null && command.Contains(ThrowOn, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("command not found");
            }

            if (command.Contains("python", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("Python 3.12.4");
            }

            if (command.Contains("git", StringComparison.OrdinalIgnoreCase))
            {
                if (command.Contains("rev-parse", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult("true");
                }

                return Task.FromResult("git version 2.45.0.windows.1");
            }

            if (command.Contains("docker", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult("Docker version 27.1.1, build 6312585");
            }

            return Task.FromResult(VersionOutput);
        }
    }

    private sealed class FakeDoctorClient : ILlamaClient
    {
        public string LlamaUrl { get; set; } = "http://localhost:11434/api/generate";

        public string SelectedModel { get; } = "qwen3:8b";

        public string? Version { get; set; } = "0.32.5";

        public Task<string?> GetServerVersionAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Version);
        }

        public Task<string> GetResponseAsync(string prompt)
        {
            return Task.FromResult(prompt);
        }

        public Task<string> GetResponseAsync(
            string prompt,
            IProgress<LlamaStreamUpdate>? progress,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(prompt);
        }

        public Task<LlamaRuntimeState> GetRuntimeStateAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlamaRuntimeState
            {
                GenerateUrl = LlamaUrl,
                ApiBaseUrl = "http://localhost:11434/api",
                SelectedModel = SelectedModel,
                IsAvailable = true,
                SelectedModelInstalled = true
            });
        }

        public Task<IReadOnlyList<LlamaModelInfo>> GetInstalledModelsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LlamaModelInfo>>([]);
        }

        public Task<bool> SelectModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<LlamaPullResult> PullModelAsync(
            string modelName,
            bool activateAfterInstall = false,
            IProgress<LlamaPullProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlamaPullResult
            {
                ModelName = modelName,
                Success = true
            });
        }
    }
}
