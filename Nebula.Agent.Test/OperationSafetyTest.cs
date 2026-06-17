using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Services.Operations;
using Nebula.Services.Safety;

namespace Nebula.Agent.Test;

public sealed class OperationSafetyTest
{
    [Fact]
    public void detector_must_classify_script_content_without_treating_it_as_terminal()
    {
        var detector = new OperationKindDetector();
        var step = new AgentStep
        {
            OriginalText = "Crie um script Python",
            Objective = "Create script",
            Content =
                "json.dumps({'mensagem': 'Hello World'}, ensure_ascii=False).encode('utf-8').decode('utf-8')",
            TargetPath = "hello.py",
            Language = "python"
        };

        Assert.Equal(OperationKind.ScriptContent, detector.Detect(step));
    }

    [Fact]
    public void python_hello_world_and_json_dumps_must_be_allowed()
    {
        var target = ControlledTempPath("hello.py");
        var classifier = new ScriptContentSafetyClassifier();

        var helloWorld = classifier.Classify(
            "print('Hello World')",
            "python",
            target);
        var json = classifier.Classify(
            """
            import json
            print(json.dumps({"mensagem": "Hello World"}, ensure_ascii=False))
            """,
            "python",
            target);

        Assert.Equal(CommandIntent.SafeWriteLocal, helloWorld.Intent);
        Assert.True(helloWorld.Confidence >= 0.95);
        Assert.Equal(CommandIntent.SafeWriteLocal, json.Intent);
        Assert.True(json.Confidence >= 0.95);
    }

    [Fact]
    public void python_subprocess_must_require_approval()
    {
        var classification = new ScriptContentSafetyClassifier().Classify(
            """
            import subprocess
            subprocess.run("dir", shell=True)
            """,
            "python",
            ControlledTempPath("subprocess.py"));

        Assert.Equal(CommandIntent.NeedsApproval, classification.Intent);
    }

    [Fact]
    public void python_api_outside_allowlist_must_require_approval()
    {
        var classification = new ScriptContentSafetyClassifier().Classify(
            """print(open("outside.txt").read())""",
            "python",
            ControlledTempPath("read_file.py"));

        Assert.Equal(CommandIntent.NeedsApproval, classification.Intent);
    }

    [Fact]
    public void csharp_process_start_must_require_approval()
    {
        var classification = new ScriptContentSafetyClassifier().Classify(
            """
            using System.Diagnostics;
            Process.Start("cmd.exe");
            """,
            "csharp",
            ControlledTempPath("process.cs"));

        Assert.Equal(CommandIntent.NeedsApproval, classification.Intent);
    }

    [Fact]
    public void destructive_os_system_must_be_blocked()
    {
        var classification = new ScriptContentSafetyClassifier().Classify(
            """
            import os
            os.system("rm -rf /")
            """,
            "python",
            ControlledTempPath("danger.py"));

        Assert.Equal(CommandIntent.Blocked, classification.Intent);
    }

    [Fact]
    public async Task missing_ml_model_must_warn_without_blocking_safe_python()
    {
        var logs = new List<string>();
        var scriptPath = ControlledTempPath("json_test.py");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(
            scriptPath,
            """
            import json
            print(json.dumps({"mensagem": "Hello World"}, ensure_ascii=False))
            """);

        var scriptClassifier = new ScriptContentSafetyClassifier();
        var deterministic = new DeterministicCommandClassifier(
            Environment.CurrentDirectory,
            scriptClassifier);
        var ml = new MlNetCommandClassifier(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip"),
            logs.Add);
        var policy = new CommandPolicyEngine(
            new CompositeCommandClassifier(deterministic, ml));

        await ml.ClassifyAsync("an ambiguous command");
        var decision = await policy.EvaluateAsync(
            $"python \"{scriptPath}\"");

        Assert.Equal(CommandSafetyDecisionType.Allow, decision.Decision);
        Assert.Contains(
            logs,
            message => message.Contains(
                "ML.NET safety model not found. Falling back to deterministic rules.",
                StringComparison.Ordinal));
    }

    private static string ControlledTempPath(string fileName) =>
        Path.Combine(
            Path.GetTempPath(),
            "Nebula",
            "tests",
            Guid.NewGuid().ToString("N"),
            fileName);
}
