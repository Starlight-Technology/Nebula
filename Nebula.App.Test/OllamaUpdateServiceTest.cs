using Nebula.App.Shared.Setup;
using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.App.Test;

public sealed class OllamaUpdateServiceTest
{
    [Fact]
    public async Task get_server_version_must_return_client_version()
    {
        var client = new FakeVersionClient { Version = "0.24.0" };
        var service = new OllamaUpdateService(client, new FakeShellExecutor());

        var version = await service.GetServerVersionAsync();

        Assert.Equal("0.24.0", version);
    }

    [Fact]
    public async Task update_server_must_pull_and_recreate_ollama_container()
    {
        var client = new FakeVersionClient { Version = "0.23.1" };
        var shell = new FakeShellExecutor();
        var service = new OllamaUpdateService(client, shell);

        var result = await service.UpdateServerAsync();

        Assert.True(result.Success);
        Assert.Equal("0.23.1", result.PreviousVersion);
        Assert.Equal("0.24.0", result.NewVersion);
        Assert.Contains(
            "docker compose pull ollama",
            result.OutputLines);
        Assert.Contains(
            "docker compose up -d ollama",
            result.OutputLines);
        Assert.Equal(2, shell.ExecutedCommands.Count);
        Assert.Contains("pull ollama", shell.ExecutedCommands[0]);
        Assert.Contains("up -d ollama", shell.ExecutedCommands[1]);
    }

    [Fact]
    public async Task update_server_must_report_failure_when_pull_throws()
    {
        var client = new FakeVersionClient { Version = "0.23.1" };
        var shell = new FakeShellExecutor
        {
            PullFailure = new InvalidOperationException("daemon offline")
        };
        var service = new OllamaUpdateService(client, shell);

        var result = await service.UpdateServerAsync();

        Assert.False(result.Success);
        Assert.Contains("daemon offline", result.Message);
        Assert.Equal("0.23.1", result.PreviousVersion);
    }

    private sealed class FakeVersionClient : ILlamaClient
    {
        public string LlamaUrl { get; set; } = "http://localhost:11434/api/generate";

        public string SelectedModel { get; } = "qwen3:8b";

        public string? Version { get; set; } = "0.24.0";

        public string? PostUpdateVersion { get; set; } = "0.24.0";

        public int VersionReads { get; private set; }

        public Task<string?> GetServerVersionAsync(CancellationToken cancellationToken = default)
        {
            VersionReads++;
            return Task.FromResult(VersionReads == 1 ? Version : PostUpdateVersion);
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

    private sealed class FakeShellExecutor : IShellExecutor
    {
        public Exception? PullFailure { get; set; }

        public List<string> ExecutedCommands { get; } = [];

        public Task<string> RunCommandAsync(string command)
        {
            return RunCommandAsync(command, CancellationToken.None);
        }

        public Task<string> RunCommandAsync(string command, CancellationToken cancellationToken)
        {
            ExecutedCommands.Add(command);
            if (PullFailure is not null && command.Contains("pull", StringComparison.OrdinalIgnoreCase))
            {
                throw PullFailure;
            }

            return Task.FromResult(command.Contains("pull", StringComparison.OrdinalIgnoreCase)
                ? "Pulling ollama..."
                : "Container ollama  Started");
        }
    }
}
