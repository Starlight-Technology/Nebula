using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.ML;
using Microsoft.ML.Data;

using Nebula.Core.MachineLearning;
using Nebula.Services.Safety;

namespace Nebula.Services.Safety.Training;

public sealed record CommandSafetyTrainingResult(
    int Version,
    byte[] ModelData,
    MlModelMetrics Metrics,
    bool Activated);

public sealed class CommandSafetyTrainer
{
    private readonly MLContext mlContext;

    public CommandSafetyTrainer(int seed = 42)
    {
        mlContext = new MLContext(seed);
    }

    public string Train(string trainingDataPath, string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        var result = TrainModel(trainingDataPath, version: 1);
        var fullModelPath = Path.GetFullPath(modelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullModelPath)!);
        File.WriteAllBytes(fullModelPath, result.ModelData);
        return fullModelPath;
    }

    public async Task<CommandSafetyTrainingResult> TrainAndSaveAsync(
        string trainingDataPath,
        int version,
        IMlModelStore modelStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelStore);

        var result = TrainModel(trainingDataPath, version);
        var activeMetrics = await modelStore.GetActiveMetricsAsync(
            MlNetCommandClassifier.ModelName,
            cancellationToken);
        var activate = result.Metrics.IsBetterThan(activeMetrics);

        await modelStore.SaveModelAsync(
            MlNetCommandClassifier.ModelName,
            version,
            result.ModelData,
            result.Metrics,
            activate,
            cancellationToken);

        return result with { Activated = activate };
    }

    private CommandSafetyTrainingResult TrainModel(
        string trainingDataPath,
        int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainingDataPath);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                "Model version must be greater than zero.");
        }

        var data = mlContext.Data.LoadFromTextFile<CommandTrainingData>(
            trainingDataPath,
            hasHeader: true,
            separatorChar: ',',
            allowQuoting: true,
            trimWhitespace: true);
        var pipeline = CreatePipeline();
        var split = mlContext.Data.TrainTestSplit(
            data,
            testFraction: 0.2,
            seed: 42);
        var evaluationModel = pipeline.Fit(split.TrainSet);
        var predictions = evaluationModel.Transform(split.TestSet);
        var evaluation = mlContext.MulticlassClassification.Evaluate(
            predictions,
            labelColumnName: "Label",
            predictedLabelColumnName: "PredictedLabel");
        var finalModel = pipeline.Fit(data);

        using var stream = new MemoryStream();
        mlContext.Model.Save(finalModel, data.Schema, stream);

        var metrics = new MlModelMetrics(
            evaluation.MicroAccuracy,
            CalculateMacroF1(evaluation.ConfusionMatrix),
            CalculateFileHash(trainingDataPath),
            SerializeSchema(data.Schema));
        return new CommandSafetyTrainingResult(
            version,
            stream.ToArray(),
            metrics,
            Activated: false);
    }

    private IEstimator<ITransformer> CreatePipeline() =>
        mlContext.Transforms.Conversion.MapValueToKey(
                outputColumnName: "Label",
                inputColumnName: nameof(CommandTrainingData.Label))
            .Append(mlContext.Transforms.Text.FeaturizeText(
                outputColumnName: "Features",
                inputColumnName: nameof(CommandTrainingData.CommandText)))
            .Append(mlContext.MulticlassClassification.Trainers
                .SdcaMaximumEntropy(
                    labelColumnName: "Label",
                    featureColumnName: "Features"))
            .Append(mlContext.Transforms.Conversion.MapKeyToValue(
                outputColumnName: "PredictedLabel"));

    private static double CalculateMacroF1(ConfusionMatrix confusionMatrix)
    {
        var counts = confusionMatrix.Counts;
        if (counts.Count == 0)
        {
            return 0;
        }

        var totalF1 = 0d;
        for (var classIndex = 0; classIndex < counts.Count; classIndex++)
        {
            var truePositive = counts[classIndex][classIndex];
            var falsePositive = counts
                .Where((_, rowIndex) => rowIndex != classIndex)
                .Sum(row => row[classIndex]);
            var falseNegative = counts[classIndex]
                .Where((_, columnIndex) => columnIndex != classIndex)
                .Sum();
            var denominator =
                (2 * truePositive) + falsePositive + falseNegative;
            totalF1 += denominator == 0
                ? 0
                : (2 * truePositive) / denominator;
        }

        return totalF1 / counts.Count;
    }

    private static string CalculateFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string SerializeSchema(DataViewSchema schema) =>
        JsonSerializer.Serialize(
            schema.Select(column => new
            {
                column.Name,
                Type = column.Type.ToString()
            }));
}
