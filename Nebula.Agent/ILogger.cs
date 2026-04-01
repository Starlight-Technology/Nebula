namespace Nebula.Agent;

/// <summary>
/// Responsible for logging messages.
/// </summary>
public interface ILogger
{
    void Log(string message);
    void LogError(string message);
}
