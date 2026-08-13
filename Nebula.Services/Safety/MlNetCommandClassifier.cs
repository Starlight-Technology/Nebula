using Microsoft.ML;

using Nebula.Core.MachineLearning;
using Nebula.Core.Safety;
using Nebula.Services.Safety.Training;

namespace Nebula.Services.Safety;

public sealed class MlNetCommandClassifier : ICommandClassifier
{
    public const string ModelName = "command-safety-classifier";

    private readonly object predictionLock = new();
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly IMlModelStore? modelStore;
    private readonly Action<string>? log;
    private PredictionEngine<CommandTrainingData, CommandPrediction>? predictionEngine;
    private volatile bool initializationAttempted;

    public MlNetCommandClassifier(
        string? fallbackModelPath = null,
        Action<string>? log = null)
        : this(null, fallbackModelPath, log)
    {
    }

    public MlNetCommandClassifier(
        IMlModelStore? modelStore,
        string? fallbackModelPath = null,
        Action<string>? log = null)
    {
        this.modelStore = modelStore;
        this.log = log;
        FallbackModelPath = ResolveModelPath(
            fallbackModelPath ?? GetDefaultModelPath());
    }

    public string FallbackModelPath { get; }

    public string ModelPath => FallbackModelPath;

    public bool IsAvailable => predictionEngine is not null;

    public async Task<CommandClassification> ClassifyAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureModelLoadedAsync(cancellationToken);

        if (predictionEngine is null)
        {
            return new CommandClassification(
                commandText,
                CommandIntent.Unknown,
                0,
                nameof(MlNetCommandClassifier),
                ["No ML.NET command model is available; deterministic rules remain active."]);
        }

        CommandPrediction prediction;
        lock (predictionLock)
        {
            prediction = predictionEngine.Predict(
                new CommandTrainingData { CommandText = commandText });
        }

        var parsed = Enum.TryParse<CommandIntent>(
            prediction.PredictedLabel,
            ignoreCase: true,
            out var intent);
        var confidence = prediction.Score.Length == 0
            ? 0
            : prediction.Score.Max();
        return new CommandClassification(
            commandText,
            parsed ? intent : CommandIntent.Unknown,
            confidence,
            nameof(MlNetCommandClassifier),
            [$"ML.NET predicted label '{prediction.PredictedLabel}'."]);
    }

    public static string GetDefaultModelPath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "models",
            $"{ModelName}.zip");

    private async Task EnsureModelLoadedAsync(CancellationToken cancellationToken)
    {
        if (initializationAttempted)
        {
            return;
        }

        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (initializationAttempted)
            {
                return;
            }

            var loaded =
                await TryLoadFromStoreAsync(cancellationToken) ||
                TryLoadFromFile();
            initializationAttempted = true;
            if (loaded)
            {
                return;
            }

            log?.Invoke(
                "Warning: ML.NET safety model not found. Falling back to deterministic rules. " +
                $"Checked PostgreSQL and fallback path '{FallbackModelPath}'.");
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private async Task<bool> TryLoadFromStoreAsync(
        CancellationToken cancellationToken)
    {
        if (modelStore is null)
        {
            return false;
        }

        try
        {
            var modelData = await modelStore.GetActiveModelAsync(
                ModelName,
                cancellationToken);
            if (modelData is null || modelData.Length == 0)
            {
                return false;
            }

            using var stream = new MemoryStream(modelData, writable: false);
            LoadModel(stream);
            log?.Invoke(
                $"ML.NET command safety model '{ModelName}' loaded from PostgreSQL.");
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke(
                "Warning: The ML.NET command safety model could not be loaded " +
                $"from PostgreSQL ({ex.Message}). Trying the fallback file.");
            return false;
        }
    }

    private bool TryLoadFromFile()
    {
        if (!File.Exists(FallbackModelPath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(FallbackModelPath);
            LoadModel(stream);
            log?.Invoke(
                "ML.NET command safety model loaded from fallback file " +
                $"'{FallbackModelPath}'.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke(
                "Warning: The ML.NET command safety fallback file " +
                $"'{FallbackModelPath}' could not be loaded ({ex.Message}). " +
                "Deterministic rules remain active.");
            return false;
        }
    }

    private void LoadModel(Stream stream)
    {
        var mlContext = new MLContext();
        var model = mlContext.Model.Load(stream, out _);
        predictionEngine =
            mlContext.Model.CreatePredictionEngine
                <CommandTrainingData, CommandPrediction>(model);
    }

    private static string ResolveModelPath(string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, AppContext.BaseDirectory);
}
