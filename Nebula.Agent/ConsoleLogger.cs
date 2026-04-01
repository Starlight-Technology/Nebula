namespace Nebula.Agent;

/// <summary>
/// Logger implementation that writes to the console.
/// </summary>
public class ConsoleLogger : ILogger
{
    public void Log(string message)
    {
        Console.WriteLine(message);
    }

    public void LogError(string message)
    {
        Console.WriteLine(message);
    }
}
