using Nebula.App.Shared.State;

namespace Nebula.App.Test;

public sealed class DatabaseInitializationStateTest
{
    [Fact]
    public void MarkMethodsUpdateStatusAndRaiseChanged()
    {
        var state = new DatabaseInitializationState();
        var changes = 0;
        state.Changed += () => changes++;

        state.MarkInitializing(2);

        Assert.Equal(DatabaseInitializationStatus.Initializing, state.Status);
        Assert.Equal(2, state.Attempts);
        Assert.Null(state.ErrorMessage);

        state.MarkFailed("offline", 2);

        Assert.Equal(DatabaseInitializationStatus.Failed, state.Status);
        Assert.Equal(2, state.Attempts);
        Assert.Equal("offline", state.ErrorMessage);

        state.MarkReady();

        Assert.Equal(DatabaseInitializationStatus.Ready, state.Status);
        Assert.Null(state.ErrorMessage);
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task RetryAsyncInvokesConfiguredHandler()
    {
        var state = new DatabaseInitializationState();
        var calls = 0;

        state.SetRetryHandler(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await state.RetryAsync();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetryAsyncDoesNotRunWhileInitializing()
    {
        var state = new DatabaseInitializationState();
        var calls = 0;

        state.SetRetryHandler(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });
        state.MarkInitializing(1);

        await state.RetryAsync();

        Assert.Equal(0, calls);
    }
}
