using Microsoft.ML.Data;

namespace Nebula.Services.Safety.Training;

public sealed class CommandPrediction
{
    [ColumnName("PredictedLabel")]
    public string PredictedLabel { get; set; } = string.Empty;

    public float[] Score { get; set; } = [];
}
