using Microsoft.ML;

using Nebula.Core.Safety;
using Nebula.Services.Safety.Training;

namespace Nebula.Services.Safety;

public sealed class MlNetCommandClassifier : ICommandClassifier
{
    private readonly object predictionLock = new();
    private readonly PredictionEngine<CommandTrainingData, CommandPrediction>? predictionEngine;
    private readonly Action<string>? log;

    public MlNetCommandClassifier(
        string? modelPath = null,
        Action<string>? log = null)
    {
        this.log = log;
        ModelPath = Path.GetFullPath(modelPath ?? GetDefaultModelPath());
        if (!File.Exists(ModelPath))
        {
            log?.Invoke(
                $"Warning: ML.NET command model was not found at '{ModelPath}'. " +
                "Deterministic rules remain active.");
            return;
        }

        var mlContext = new MLContext();
        var model = mlContext.Model.Load(ModelPath, out _);
        predictionEngine = mlContext.Model.CreatePredictionEngine<CommandTrainingData, CommandPrediction>(model);
    }

    public string ModelPath { get; }

    public bool IsAvailable => predictionEngine is not null;

    public Task<CommandClassification> ClassifyAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (predictionEngine is null)
        {
            return Task.FromResult(new CommandClassification(
                commandText,
                CommandIntent.Unknown,
                0,
                nameof(MlNetCommandClassifier),
                [$"ML.NET model was not found at '{ModelPath}'; deterministic rules remain active."]));
        }

        CommandPrediction prediction;
        lock (predictionLock)
        {
            prediction = predictionEngine.Predict(new CommandTrainingData { CommandText = commandText });
        }

        var parsed = Enum.TryParse<CommandIntent>(prediction.PredictedLabel, ignoreCase: true, out var intent);
        var confidence = prediction.Score.Length == 0 ? 0 : prediction.Score.Max();
        return Task.FromResult(new CommandClassification(
            commandText,
            parsed ? intent : CommandIntent.Unknown,
            confidence,
            nameof(MlNetCommandClassifier),
            [$"ML.NET predicted label '{prediction.PredictedLabel}'."]));
    }

    public static string GetDefaultModelPath() =>
        Path.Combine(AppContext.BaseDirectory, "models", "command-safety-classifier.zip");
}
