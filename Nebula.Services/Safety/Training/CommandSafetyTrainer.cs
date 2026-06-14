using Microsoft.ML;

namespace Nebula.Services.Safety.Training;

public sealed class CommandSafetyTrainer
{
    private readonly MLContext mlContext;

    public CommandSafetyTrainer(int seed = 42)
    {
        mlContext = new MLContext(seed);
    }

    public string Train(string trainingDataPath, string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainingDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        var data = mlContext.Data.LoadFromTextFile<CommandTrainingData>(
            trainingDataPath,
            hasHeader: true,
            separatorChar: ',',
            allowQuoting: true,
            trimWhitespace: true);

        var pipeline = mlContext.Transforms.Conversion.MapValueToKey(
                outputColumnName: "Label",
                inputColumnName: nameof(CommandTrainingData.Label))
            .Append(mlContext.Transforms.Text.FeaturizeText(
                outputColumnName: "Features",
                inputColumnName: nameof(CommandTrainingData.CommandText)))
            .Append(mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy(
                labelColumnName: "Label",
                featureColumnName: "Features"))
            .Append(mlContext.Transforms.Conversion.MapKeyToValue(
                outputColumnName: "PredictedLabel"));

        var model = pipeline.Fit(data);
        var fullModelPath = Path.GetFullPath(modelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullModelPath)!);
        mlContext.Model.Save(model, data.Schema, fullModelPath);
        return fullModelPath;
    }
}
