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

    [Fact]
    public void file_write_classifier_with_custom_workspace_must_allow_paths_inside_it()
    {
        using var workspace = new TempTestWorkspace();
        var classifier = new FileWriteSafetyClassifier(
            workspaceRoot: workspace.Path);

        var classification = classifier.Classify(
            Path.Combine(workspace.Path, "src", "hello.txt"));

        Assert.Equal(CommandIntent.SafeWriteLocal, classification.Intent);
    }

    [Fact]
    public void file_write_classifier_with_custom_workspace_must_require_approval_outside_it()
    {
        using var workspace = new TempTestWorkspace();
        var classifier = new FileWriteSafetyClassifier(
            workspaceRoot: workspace.Path);

        var classification = classifier.Classify(
            Path.Combine(
                Path.GetTempPath(),
                "outside-workspace",
                "hello.txt"));

        Assert.Equal(CommandIntent.NeedsApproval, classification.Intent);
    }

    [Fact]
    public void deterministic_classifier_with_custom_workspace_must_allow_local_writes_inside_it()
    {
        using var workspace = new TempTestWorkspace();
        const string fileName = "nota.txt";
        var classifier = new DeterministicCommandClassifier(
            workspaceRoot: workspace.Path);

        var classification = classifier.ClassifyAsync(
            $"echo 'nota' > {Path.Combine(workspace.Path, fileName)}")
            .GetAwaiter()
            .GetResult();

        Assert.Equal(CommandIntent.SafeWriteLocal, classification.Intent);
    }

    private static string ControlledTempPath(string fileName) =>
        Path.Combine(
            Path.GetTempPath(),
            "Nebula",
            "tests",
            Guid.NewGuid().ToString("N"),
            fileName);
}
