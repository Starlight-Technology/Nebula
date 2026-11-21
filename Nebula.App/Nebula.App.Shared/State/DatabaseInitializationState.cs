namespace Nebula.App.Shared.State;

public enum DatabaseInitializationStatus
{
    Pending,
    Initializing,
    Ready,
    Failed
}

public sealed class DatabaseInitializationState
{
    private readonly object sync = new();
    private Func<CancellationToken, Task>? retryHandler;

    public DatabaseInitializationStatus Status { get; private set; } =
        DatabaseInitializationStatus.Pending;

    public string? ErrorMessage { get; private set; }

    public int Attempts { get; private set; }

    public event Action? Changed;

    public void SetRetryHandler(Func<CancellationToken, Task>? handler)
    {
        lock (sync)
        {
            retryHandler = handler;
        }
    }

    public void MarkInitializing(int attempt)
    {
        lock (sync)
        {
            Status = DatabaseInitializationStatus.Initializing;
            Attempts = attempt;
            ErrorMessage = null;
        }

        Changed?.Invoke();
    }

    public void MarkReady()
    {
        lock (sync)
        {
            Status = DatabaseInitializationStatus.Ready;
            ErrorMessage = null;
        }

        Changed?.Invoke();
    }

    public void MarkFailed(string? errorMessage, int attempts)
    {
        lock (sync)
        {
            Status = DatabaseInitializationStatus.Failed;
            Attempts = attempts;
            ErrorMessage = errorMessage;
        }

        Changed?.Invoke();
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task>? handler;

        lock (sync)
        {
            if (Status == DatabaseInitializationStatus.Initializing)
            {
                return;
            }

            handler = retryHandler;
        }

        if (handler is not null)
        {
            await handler(cancellationToken);
        }
    }
}
