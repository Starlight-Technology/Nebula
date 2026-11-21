using Microsoft.Extensions.Logging;

using Nebula.Agent;
using Nebula.Agent.Logging;

using MelLogger = Microsoft.Extensions.Logging.ILogger;

namespace Nebula.Agent.Test.Logging;

public sealed class RedactionLoggingTest
{
    [Fact]
    public void ConsoleLogger_redacts_log_and_error_messages()
    {
        using var writer = new StringWriter();
        var logger = new ConsoleLogger(writer);

        logger.Log("password: supersecret123");
        logger.LogError("Authorization: Bearer abcdef1234567890abcdef");

        var output = writer.ToString();

        Assert.DoesNotContain("supersecret123", output);
        Assert.DoesNotContain("abcdef1234567890abcdef", output);
        Assert.Contains("password: ***", output);
        Assert.Contains("Authorization: Bearer ***", output);
    }

    [Fact]
    public void RedactingLoggerProvider_redacts_formatted_message_and_exception()
    {
        var messages = new List<string>();
        using var provider = new RedactingLoggerProvider(logging =>
            logging.AddProvider(new CapturingLoggerProvider(messages)));
        var logger = provider.CreateLogger("test");

        logger.LogInformation("token=supersecret123");
        logger.LogError(
            new InvalidOperationException(
                "password: supersecret456",
                new InvalidOperationException("token=supersecret789")),
            "command failed");

        Assert.DoesNotContain(
            messages,
            message => message.Contains("supersecret123", StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("supersecret456", StringComparison.Ordinal));
        Assert.DoesNotContain(
            messages,
            message => message.Contains("supersecret789", StringComparison.Ordinal));
        Assert.Contains(
            messages,
            message => message.Contains("token=***", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public MelLogger CreateLogger(string categoryName)
            => new CapturingLogger(messages);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(List<string> messages) : MelLogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                messages.Add(exception.ToString());
            }

            if (exception?.InnerException is not null)
            {
                messages.Add(exception.InnerException.ToString());
            }
        }
    }
}
