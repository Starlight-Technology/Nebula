using System.Globalization;

namespace Nebula.Postgres.Context;

public sealed class DatabaseInitializationOptions
{
    public int MaxAttempts { get; init; } = 5;

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    public static DatabaseInitializationOptions FromEnvironment()
    {
        return new DatabaseInitializationOptions
        {
            MaxAttempts = ReadPositiveInt(
                "NEBULA_DB_INIT_MAX_ATTEMPTS",
                defaultValue: 5,
                minimum: 1,
                maximum: 20),
            InitialDelay = TimeSpan.FromMilliseconds(ReadPositiveInt(
                "NEBULA_DB_INIT_INITIAL_DELAY_MS",
                defaultValue: 1000,
                minimum: 100,
                maximum: 60000)),
            MaxDelay = TimeSpan.FromMilliseconds(ReadPositiveInt(
                "NEBULA_DB_INIT_MAX_DELAY_MS",
                defaultValue: 30000,
                minimum: 100,
                maximum: 300000))
        };
    }

    private static int ReadPositiveInt(
        string variableName,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var rawValue = Environment.GetEnvironmentVariable(variableName);
        if (!int.TryParse(
                rawValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedValue))
        {
            return defaultValue;
        }

        return Math.Clamp(parsedValue, minimum, maximum);
    }
}

public static class DatabaseInitializationRetry
{
    public static async Task ExecuteAsync(
        Func<CancellationToken, Task> attempt,
        DatabaseInitializationOptions? options = null,
        Action<Exception, int>? onAttemptFailed = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        options ??= new DatabaseInitializationOptions();
        delay ??= Task.Delay;

        var maxAttempts = Math.Max(1, options.MaxAttempts);
        var initialDelay = options.InitialDelay < TimeSpan.Zero
            ? TimeSpan.Zero
            : options.InitialDelay;
        var maxDelay = options.MaxDelay < initialDelay
            ? initialDelay
            : options.MaxDelay;

        cancellationToken.ThrowIfCancellationRequested();

        for (var attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
        {
            try
            {
                await attempt(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                onAttemptFailed?.Invoke(exception, attemptNumber);

                if (attemptNumber >= maxAttempts)
                {
                    throw;
                }

                var delayMilliseconds = Math.Min(
                    initialDelay.TotalMilliseconds * Math.Pow(2, attemptNumber - 1),
                    maxDelay.TotalMilliseconds);

                await delay(
                        TimeSpan.FromMilliseconds(delayMilliseconds),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
