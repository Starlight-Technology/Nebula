namespace Nebula.Core.MachineLearning;

public interface IMlModelStore
{
    Task<byte[]?> GetActiveModelAsync(
        string modelName,
        CancellationToken ct = default);

    Task<MlModelMetrics?> GetActiveMetricsAsync(
        string modelName,
        CancellationToken ct = default);

    Task SaveModelAsync(
        string modelName,
        int version,
        byte[] modelData,
        MlModelMetrics? metrics,
        bool activate,
        CancellationToken ct = default);

    Task ActivateModelAsync(
        Guid modelId,
        CancellationToken ct = default);
}
