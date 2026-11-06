using Nebula.Postgres.Context;

namespace Nebula.Agent.Test;

public sealed class DatabaseInitializationRetryTest
{
    [Fact]
    public async Task ExecuteAsync_RetriesUntilSuccess()
    {
        var attempts = 0;
        var failures = 0;
        var delays = new List<TimeSpan>();

        await DatabaseInitializationRetry.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new InvalidOperationException("transient");
                }

                return Task.CompletedTask;
            },
            new DatabaseInitializationOptions
            {
                MaxAttempts = 5,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(30)
            },
            (_, _) => failures++,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal(3, attempts);
        Assert.Equal(2, failures);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)],
            delays);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsAfterMaxAttempts()
    {
        var attempts = 0;
        var failures = 0;
        var delays = new List<TimeSpan>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializationRetry.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException("unavailable");
                },
                new DatabaseInitializationOptions
                {
                    MaxAttempts = 3,
                    InitialDelay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(30)
                },
                (_, _) => failures++,
                (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                }));

        Assert.Equal("unavailable", exception.Message);
        Assert.Equal(3, attempts);
        Assert.Equal(3, failures);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)],
            delays);
    }

    [Fact]
    public async Task ExecuteAsync_CapsDelayAtMaximum()
    {
        var delays = new List<TimeSpan>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseInitializationRetry.ExecuteAsync(
                _ => throw new InvalidOperationException("unavailable"),
                new DatabaseInitializationOptions
                {
                    MaxAttempts = 4,
                    InitialDelay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(3)
                },
                delay: (delay, _) =>
                {
                    delays.Add(delay);
                    return Task.CompletedTask;
                }));

        Assert.Equal(
            [
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3)
            ],
            delays);
    }

    [Fact]
    public async Task ExecuteAsync_HonorsCancellation()
    {
        var operationCalled = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseInitializationRetry.ExecuteAsync(
                _ =>
                {
                    operationCalled = true;
                    return Task.CompletedTask;
                },
                cancellationToken: cancellation.Token));

        Assert.False(operationCalled);
    }

    [Fact]
    public async Task ExecuteAsync_NormalizesNonPositiveMaxAttempts()
    {
        var attempts = 0;

        await DatabaseInitializationRetry.ExecuteAsync(
            _ =>
            {
                attempts++;
                return Task.CompletedTask;
            },
            new DatabaseInitializationOptions { MaxAttempts = 0 });

        Assert.Equal(1, attempts);
    }
}
