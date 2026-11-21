using Nebula.Core.Safety;

namespace Nebula.Agent;

/// <summary>
/// Logger implementation that writes to the console.
/// </summary>
public class ConsoleLogger : ILogger
{
    private readonly TextWriter _output;

    public ConsoleLogger() : this(Console.Out)
    {
    }

    internal ConsoleLogger(TextWriter output)
    {
        _output = output;
    }

    public void Log(string message)
    {
        _output.WriteLine(SecretRedaction.Apply(message));
    }

    public void LogError(string message)
    {
        _output.WriteLine(SecretRedaction.Apply(message));
    }
}
