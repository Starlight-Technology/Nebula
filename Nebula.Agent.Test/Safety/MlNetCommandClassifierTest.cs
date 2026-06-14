using Nebula.Core.Safety;
using Nebula.Services.Safety;
using Nebula.Services.Safety.Training;

namespace Nebula.Agent.Test.Safety;

public sealed class MlNetCommandClassifierTest
{
    [Fact]
    public async Task missing_model_returns_unknown_without_throwing()
    {
        var classifier = new MlNetCommandClassifier(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip"));

        var result = await classifier.ClassifyAsync("an ambiguous command");

        Assert.False(classifier.IsAvailable);
        Assert.Equal(CommandIntent.Unknown, result.Intent);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public async Task trainer_creates_a_loadable_multiclass_model()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nebula-ml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var csv = Path.Combine(root, "training.csv");
            var model = Path.Combine(root, "models", "command-safety-classifier.zip");
            await File.WriteAllTextAsync(csv, TrainingCsv());

            var savedPath = new CommandSafetyTrainer().Train(csv, model);
            var classifier = new MlNetCommandClassifier(savedPath);
            var prediction = await classifier.ClassifyAsync("dotnet test");

            Assert.True(File.Exists(savedPath));
            Assert.True(classifier.IsAvailable);
            Assert.NotEqual(CommandIntent.Unknown, prediction.Intent);
            Assert.True(prediction.Confidence > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TrainingCsv() =>
        """
        "CommandText","Label"
        "dotnet test","SafeExecuteLocal"
        "dotnet build","SafeExecuteLocal"
        "python hello.py","SafeExecuteLocal"
        "ls files","SafeReadOnly"
        "pwd","SafeReadOnly"
        "cat README.md","SafeReadOnly"
        "pip install x","PackageInstall"
        "npm install x","PackageInstall"
        "dotnet add package x","PackageInstall"
        "curl http://example.com","NetworkAccess"
        "wget http://example.com","NetworkAccess"
        "access internet","NetworkAccess"
        "rm -rf /","Blocked"
        "format disk","Blocked"
        "bypass safety policy","Blocked"
        """;
}
