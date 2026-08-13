using Nebula.Core.Safety;
using Nebula.Runner;
using Nebula.Services.Safety;

namespace Nebula.Agent.Test.Execution;

public sealed class SandboxCommandExecutionTest : IDisposable
{
    private readonly string workspace = Path.Combine(
        Path.GetTempPath(),
        "Nebula",
        "tests",
        $"execution-{Guid.NewGuid():N}");
    private readonly ICommandPolicyEngine policy;
    private readonly ShellExecutor executor = new();

    public SandboxCommandExecutionTest()
    {
        Directory.CreateDirectory(workspace);
        policy = CreatePolicy(workspace);
    }

    [Fact]
    public async Task safe_commands_execute_only_inside_controlled_sandbox()
    {
        var createDirectory = OperatingSystem.IsWindows()
            ? "New-Item -ItemType Directory -Force -Path ./NebulaSandbox"
            : "mkdir -p ./NebulaSandbox";
        var writeFile = OperatingSystem.IsWindows()
            ? "Set-Content -Path ./NebulaSandbox/hello.txt -Value 'Hello from Nebula'"
            : "printf \"Hello from Nebula\\n\" > ./NebulaSandbox/hello.txt";
        var readFile = OperatingSystem.IsWindows()
            ? "Get-Content ./NebulaSandbox/hello.txt"
            : "cat ./NebulaSandbox/hello.txt";
        var listDirectory = OperatingSystem.IsWindows()
            ? "Get-ChildItem -LiteralPath ./NebulaSandbox"
            : "ls -la ./NebulaSandbox";

        await AssertAllowedThenRunAsync("echo Hello Nebula");
        await AssertAllowedThenRunAsync(createDirectory);
        await AssertAllowedThenRunAsync(writeFile);
        var read = await AssertAllowedThenRunAsync(readFile);
        var list = await AssertAllowedThenRunAsync(listDirectory);

        Assert.Contains("Hello from Nebula", read.StandardOutput);
        Assert.Contains("hello.txt", list.StandardOutput);
        Assert.True(File.Exists(Path.Combine(workspace, "NebulaSandbox", "hello.txt")));
    }

    [Fact]
    public async Task python_hello_script_executes_when_interpreter_is_available()
    {
        var scriptPath = Path.Combine(workspace, "hello.py");
        await File.WriteAllTextAsync(
            scriptPath,
            "print('Hello from Nebula')",
            CancellationToken.None);
        var python = await FindPythonAsync();
        if (python is null)
        {
            return;
        }

        var result = await AssertAllowedThenRunAsync($"{python} hello.py");

        Assert.Contains("Hello from Nebula", result.StandardOutput);
    }

    private async Task<ShellCommandResult> AssertAllowedThenRunAsync(
        string command)
    {
        var decision = await policy.EvaluateAsync(
            command,
            CancellationToken.None);
        Assert.True(
            decision.Decision == CommandSafetyDecisionType.Allow,
            $"{command} => {decision.Decision}; {string.Join(" | ", decision.Reasons)}");

        var result = await executor.RunCommandDetailedAsync(
            command,
            workspace,
            CancellationToken.None);
        Assert.True(result.Success, result.CombinedOutput);
        Assert.Equal(Path.GetFullPath(workspace), result.WorkingDirectory);
        return result;
    }

    private async Task<string?> FindPythonAsync()
    {
        foreach (var candidate in new[] { "python", "py", "python3" })
        {
            var result = await executor.RunCommandDetailedAsync(
                $"{candidate} --version",
                workspace,
                CancellationToken.None);
            if (result.Success)
            {
                return candidate;
            }
        }

        return null;
    }

    private static ICommandPolicyEngine CreatePolicy(string workspace)
    {
        var deterministic = new DeterministicCommandClassifier(workspace);
        var missingModel = Path.Combine(workspace, "missing-command-safety.zip");
        var composite = new CompositeCommandClassifier(
            deterministic,
            new MlNetCommandClassifier(missingModel));
        return new CommandPolicyEngine(composite);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
