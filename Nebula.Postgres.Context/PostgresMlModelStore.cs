using Microsoft.EntityFrameworkCore;

using Nebula.Core.MachineLearning;
using Nebula.Postgres.Context.Entities;

namespace Nebula.Postgres.Context;

public sealed class PostgresMlModelStore(PostgresContext context)
    : IMlModelStore
{
    public Task<byte[]?> GetActiveModelAsync(
        string modelName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        return context.MlModelArtifacts
            .AsNoTracking()
            .Where(model => model.Name == modelName && model.IsActive)
            .OrderByDescending(model => model.Version)
            .Select(model => model.ModelData)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<MlModelMetrics?> GetActiveMetricsAsync(
        string modelName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var model = await context.MlModelArtifacts
            .AsNoTracking()
            .Where(value => value.Name == modelName && value.IsActive)
            .OrderByDescending(value => value.Version)
            .Select(value => new
            {
                value.Accuracy,
                value.F1Score,
                value.TrainingDatasetHash,
                value.SchemaJson
            })
            .FirstOrDefaultAsync(ct);

        return model is null
            ? null
            : new MlModelMetrics(
                model.Accuracy,
                model.F1Score,
                model.TrainingDatasetHash,
                model.SchemaJson);
    }

    public async Task SaveModelAsync(
        string modelName,
        int version,
        byte[] modelData,
        MlModelMetrics? metrics,
        bool activate,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(modelData);
        if (modelData.Length == 0)
        {
            throw new ArgumentException(
                "Model data cannot be empty.",
                nameof(modelData));
        }

        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(ct)
            : null;
        var now = DateTime.UtcNow;
        if (activate)
        {
            var activeModels = await context.MlModelArtifacts
                .Where(model => model.Name == modelName && model.IsActive)
                .ToListAsync(ct);
            foreach (var activeModel in activeModels)
            {
                activeModel.IsActive = false;
            }

            await context.SaveChangesAsync(ct);
        }

        context.MlModelArtifacts.Add(new MlModelArtifact
        {
            Name = modelName,
            Version = version,
            ModelData = modelData,
            SchemaJson = metrics?.SchemaJson,
            Accuracy = metrics?.Accuracy,
            F1Score = metrics?.F1Score,
            TrainingDatasetHash = metrics?.TrainingDatasetHash,
            IsActive = activate,
            CreatedAt = now,
            ActivatedAt = activate ? now : null
        });

        await context.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
    }

    public async Task ActivateModelAsync(
        Guid modelId,
        CancellationToken ct = default)
    {
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(ct)
            : null;
        var target = await context.MlModelArtifacts
            .SingleOrDefaultAsync(model => model.Id == modelId, ct)
            ?? throw new InvalidOperationException(
                $"ML.NET model artifact '{modelId}' was not found.");

        var activeModels = await context.MlModelArtifacts
            .Where(model =>
                model.Name == target.Name &&
                model.IsActive &&
                model.Id != target.Id)
            .ToListAsync(ct);
        foreach (var activeModel in activeModels)
        {
            activeModel.IsActive = false;
        }

        await context.SaveChangesAsync(ct);
        target.IsActive = true;
        target.ActivatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
    }
}
