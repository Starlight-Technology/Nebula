using Microsoft.ML.Data;

namespace Nebula.Services.Safety.Training;

public sealed class CommandTrainingData
{
    [LoadColumn(0)]
    public string CommandText { get; set; } = string.Empty;

    [LoadColumn(1)]
    public string Label { get; set; } = string.Empty;
}
