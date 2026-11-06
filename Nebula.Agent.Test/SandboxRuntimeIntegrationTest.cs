using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class SandboxRuntimeIntegrationTest
{
    [Fact]
    public async Task sandbox_must_execute_echo_with_real_runtime_when_enabled()
    {
        var sandbox = CreateSandbox();
        if (sandbox is null)
        {
            return;
        }

        var workspace = CreateWorkspace();
        try
        {
            var result = await sandbox.RunSandboxedAsync(
                ShellKind.Bash,
                new ResolvedCommand(
                    "bash",
                    "-c \"echo nebula-sandbox\"",
                    "echo nebula-sandbox",
                    workspace,
                    []),
                workspace,
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("nebula-sandbox", result.StandardOutput);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task sandbox_must_execute_script_created_in_workspace()
    {
        var sandbox = CreateSandbox();
        if (sandbox is null)
        {
            return;
        }

        var workspace = CreateWorkspace();
        try
        {
            var scriptPath = Path.Combine(workspace, "hello.sh");
            await File.WriteAllTextAsync(
                scriptPath,
                "#!/usr/bin/env bash\necho nebula-script\n");

            var result = await sandbox.RunSandboxedAsync(
                ShellKind.Bash,
                new ResolvedCommand(
                    "bash",
                    "-c \"bash hello.sh\"",
                    "bash hello.sh",
                    workspace,
                    []),
                workspace,
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("nebula-script", result.StandardOutput);
            Assert.True(File.Exists(scriptPath));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static DockerCommandSandbox? CreateSandbox()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("NEBULA_SANDBOX_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        var preference = NebulaRuntimeSettings.ParseSandboxRuntime(
            Environment.GetEnvironmentVariable(
                "NEBULA_SANDBOX_INTEGRATION_RUNTIME"));
        var locator = new SandboxRuntimeLocator();
        var runtime = locator.Locate(preference);
        Assert.NotNull(runtime);

        var image = Environment.GetEnvironmentVariable(
            "NEBULA_SANDBOX_INTEGRATION_IMAGE");
        if (string.IsNullOrWhiteSpace(image))
        {
            image = "mcr.microsoft.com/dotnet/sdk:10.0-noble";
        }

        var settings = new NebulaRuntimeSettings
        {
            SandboxMode = SandboxMode.Docker,
            SandboxRuntime = preference,
            SandboxImage = image
        };
        return new DockerCommandSandbox(
            new ShellExecutor(),
            settings,
            locator);
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"nebula-sandbox-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        return workspace;
    }
}
