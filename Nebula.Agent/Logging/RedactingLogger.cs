using Microsoft.Extensions.Logging;

using Nebula.Core.Safety;

using MelLogger = Microsoft.Extensions.Logging.ILogger;

namespace Nebula.Agent.Logging;

internal sealed class RedactingLogger(MelLogger inner) : MelLogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel)
        => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var safeException = exception is null
            ? null
            : new RedactedException(exception);

        inner.Log(
            logLevel,
            eventId,
            state,
            safeException,
            (currentState, _) =>
                SecretRedaction.Apply(formatter(currentState, exception)) ?? string.Empty);
    }

    private sealed class RedactedException : Exception
    {
        private readonly Exception _inner;

        public RedactedException(Exception inner)
            : base(
                SecretRedaction.Apply(inner.Message),
                inner.InnerException is null
                    ? null
                    : new RedactedException(inner.InnerException))
        {
            _inner = inner;
        }

        public override string? StackTrace
            => SecretRedaction.Apply(_inner.StackTrace);

        public override string ToString()
            => SecretRedaction.Apply(_inner.ToString()) ?? string.Empty;
    }
}
