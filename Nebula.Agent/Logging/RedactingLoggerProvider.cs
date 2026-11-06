using Microsoft.Extensions.Logging;

using MelLogger = Microsoft.Extensions.Logging.ILogger;

namespace Nebula.Agent.Logging;

public sealed class RedactingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory _innerFactory;

    public RedactingLoggerProvider(Action<ILoggingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _innerFactory = LoggerFactory.Create(configure);
    }

    public MelLogger CreateLogger(string categoryName)
        => new RedactingLogger(_innerFactory.CreateLogger(categoryName));

    public void Dispose()
        => _innerFactory.Dispose();
}
