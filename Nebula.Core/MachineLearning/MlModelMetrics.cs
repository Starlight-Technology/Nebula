namespace Nebula.Core.MachineLearning;

public sealed record MlModelMetrics(
    double? Accuracy = null,
    double? F1Score = null,
    string? TrainingDatasetHash = null,
    string? SchemaJson = null)
{
    public bool IsBetterThan(MlModelMetrics? other)
    {
        if (other is null)
        {
            return true;
        }

        var f1Comparison = Nullable.Compare(F1Score, other.F1Score);
        return f1Comparison > 0 ||
            (f1Comparison == 0 &&
             Nullable.Compare(Accuracy, other.Accuracy) > 0);
    }
}
