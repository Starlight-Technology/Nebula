using Microsoft.EntityFrameworkCore;

using Nebula.Core.MachineLearning;
using Nebula.Postgres.Context;
using Nebula.Services.Safety.Training;

namespace Nebula.Agent.Test.Safety;

public sealed class PostgresMlModelStoreTest
{
    [Fact]
    public async Task saving_active_version_deactivates_previous_versions()
    {
        await using var context = CreateContext();
        var store = new PostgresMlModelStore(context);

        await store.SaveModelAsync(
            "command-safety-classifier",
            version: 1,
            modelData: [1],
            metrics: new MlModelMetrics(0.7, 0.6),
            activate: true);
        await store.SaveModelAsync(
            "command-safety-classifier",
            version: 2,
            modelData: [2],
            metrics: new MlModelMetrics(0.8, 0.7),
            activate: true);

        var models = await context.MlModelArtifacts
            .OrderBy(model => model.Version)
            .ToListAsync();

        Assert.False(models[0].IsActive);
        Assert.NotNull(models[0].ActivatedAt);
        Assert.True(models[1].IsActive);
        Assert.NotNull(models[1].ActivatedAt);
        Assert.Equal([2], await store.GetActiveModelAsync(
            "command-safety-classifier"));
    }

    [Fact]
    public async Task activate_model_switches_active_version()
    {
        await using var context = CreateContext();
        var store = new PostgresMlModelStore(context);

        await store.SaveModelAsync(
            "command-safety-classifier",
            version: 1,
            modelData: [1],
            metrics: null,
            activate: true);
        await store.SaveModelAsync(
            "command-safety-classifier",
            version: 2,
            modelData: [2],
            metrics: null,
            activate: false);
        var secondModelId = await context.MlModelArtifacts
            .Where(model => model.Version == 2)
            .Select(model => model.Id)
            .SingleAsync();

        await store.ActivateModelAsync(secondModelId);

        var models = await context.MlModelArtifacts
            .OrderBy(model => model.Version)
            .ToListAsync();
        Assert.False(models[0].IsActive);
        Assert.True(models[1].IsActive);
    }

    [Fact]
    public async Task trainer_persists_loadable_model_bytes_in_store()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"nebula-postgres-ml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var csvPath = Path.Combine(root, "training.csv");
            await File.WriteAllTextAsync(csvPath, TrainingCsv());
            await using var context = CreateContext();
            var store = new PostgresMlModelStore(context);

            var result = await new CommandSafetyTrainer().TrainAndSaveAsync(
                csvPath,
                version: 3,
                store);

            var artifact = await context.MlModelArtifacts.SingleAsync();
            Assert.Equal(result.ModelData, artifact.ModelData);
            Assert.NotEmpty(artifact.ModelData);
            Assert.True(artifact.IsActive);
            Assert.NotNull(artifact.Accuracy);
            Assert.NotNull(artifact.F1Score);
            Assert.False(string.IsNullOrWhiteSpace(
                artifact.TrainingDatasetHash));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static PostgresContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PostgresContext>()
            .UseInMemoryDatabase($"nebula-ml-{Guid.NewGuid():N}")
            .Options;
        return new PostgresContext(options);
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
