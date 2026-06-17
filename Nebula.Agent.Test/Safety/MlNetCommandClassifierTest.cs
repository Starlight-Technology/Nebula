using Nebula.Core.MachineLearning;
using Nebula.Core.Safety;
using Nebula.Services.Safety;
using Nebula.Services.Safety.Training;

namespace Nebula.Agent.Test.Safety;

public sealed class MlNetCommandClassifierTest
{
    [Fact]
    public async Task loads_active_model_from_database_before_file_fallback()
    {
        using var fixture = await TrainingFixture.CreateAsync();
        var store = new RecordingModelStore
        {
            ActiveModel = await File.ReadAllBytesAsync(fixture.ModelPath)
        };
        var logs = new List<string>();
        var classifier = new MlNetCommandClassifier(
            store,
            Path.Combine(fixture.Root, "missing-fallback.zip"),
            logs.Add);

        var prediction = await classifier.ClassifyAsync("dotnet test");

        Assert.True(classifier.IsAvailable);
        Assert.NotEqual(CommandIntent.Unknown, prediction.Intent);
        Assert.Contains(
            logs,
            message => message.Contains(
                "loaded from PostgreSQL",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task loads_configured_file_when_database_has_no_active_model()
    {
        using var fixture = await TrainingFixture.CreateAsync();
        var logs = new List<string>();
        var classifier = new MlNetCommandClassifier(
            new RecordingModelStore(),
            fixture.ModelPath,
            logs.Add);

        var prediction = await classifier.ClassifyAsync("dotnet test");

        Assert.True(classifier.IsAvailable);
        Assert.NotEqual(CommandIntent.Unknown, prediction.Intent);
        Assert.Contains(
            logs,
            message => message.Contains(
                "loaded from fallback file",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task missing_model_returns_unknown_and_logs_deterministic_fallback()
    {
        var logs = new List<string>();
        var classifier = new MlNetCommandClassifier(
            new RecordingModelStore(),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip"),
            logs.Add);

        var result = await classifier.ClassifyAsync("an ambiguous command");

        Assert.False(classifier.IsAvailable);
        Assert.Equal(CommandIntent.Unknown, result.Intent);
        Assert.Equal(0, result.Confidence);
        Assert.Contains(
            logs,
            message => message.Contains(
                "ML.NET safety model not found. Falling back to deterministic rules.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task trainer_saves_serialized_model_and_metrics_to_store()
    {
        using var fixture = await TrainingFixture.CreateAsync(trainModel: false);
        var store = new RecordingModelStore();

        var result = await new CommandSafetyTrainer().TrainAndSaveAsync(
            fixture.CsvPath,
            version: 7,
            store);

        Assert.Equal(MlNetCommandClassifier.ModelName, store.SavedModelName);
        Assert.Equal(7, store.SavedVersion);
        Assert.NotNull(store.SavedModelData);
        Assert.NotEmpty(store.SavedModelData);
        Assert.NotNull(store.SavedMetrics);
        Assert.False(string.IsNullOrWhiteSpace(
            store.SavedMetrics.TrainingDatasetHash));
        Assert.False(string.IsNullOrWhiteSpace(store.SavedMetrics.SchemaJson));
        Assert.True(store.ActivateSavedModel);
        Assert.True(result.Activated);
    }

    [Fact]
    public async Task trainer_does_not_activate_model_with_worse_metrics()
    {
        using var fixture = await TrainingFixture.CreateAsync(trainModel: false);
        var store = new RecordingModelStore
        {
            ActiveMetrics = new MlModelMetrics(
                Accuracy: 1,
                F1Score: 1)
        };

        var result = await new CommandSafetyTrainer().TrainAndSaveAsync(
            fixture.CsvPath,
            version: 8,
            store);

        Assert.False(store.ActivateSavedModel);
        Assert.False(result.Activated);
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

    private sealed class RecordingModelStore : IMlModelStore
    {
        public byte[]? ActiveModel { get; init; }

        public MlModelMetrics? ActiveMetrics { get; init; }

        public string? SavedModelName { get; private set; }

        public int SavedVersion { get; private set; }

        public byte[]? SavedModelData { get; private set; }

        public MlModelMetrics? SavedMetrics { get; private set; }

        public bool ActivateSavedModel { get; private set; }

        public Task<byte[]?> GetActiveModelAsync(
            string modelName,
            CancellationToken ct = default) =>
            Task.FromResult(ActiveModel);

        public Task<MlModelMetrics?> GetActiveMetricsAsync(
            string modelName,
            CancellationToken ct = default) =>
            Task.FromResult(ActiveMetrics);

        public Task SaveModelAsync(
            string modelName,
            int version,
            byte[] modelData,
            MlModelMetrics? metrics,
            bool activate,
            CancellationToken ct = default)
        {
            SavedModelName = modelName;
            SavedVersion = version;
            SavedModelData = modelData;
            SavedMetrics = metrics;
            ActivateSavedModel = activate;
            return Task.CompletedTask;
        }

        public Task ActivateModelAsync(
            Guid modelId,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TrainingFixture : IDisposable
    {
        private TrainingFixture(
            string root,
            string csvPath,
            string modelPath)
        {
            Root = root;
            CsvPath = csvPath;
            ModelPath = modelPath;
        }

        public string Root { get; }

        public string CsvPath { get; }

        public string ModelPath { get; }

        public static async Task<TrainingFixture> CreateAsync(
            bool trainModel = true)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"nebula-ml-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var csv = Path.Combine(root, "training.csv");
            var model = Path.Combine(
                root,
                "models",
                "command-safety-classifier.zip");
            await File.WriteAllTextAsync(csv, TrainingCsv());
            if (trainModel)
            {
                new CommandSafetyTrainer().Train(csv, model);
            }

            return new TrainingFixture(root, csv, model);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
