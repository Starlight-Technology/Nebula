namespace Nebula.Agent;

public interface IManager
{
    Task<string> ManageResponse(string prompt);
    Task<string> GenerateCommandSteps(string userRequest);
    Task<bool> VerifyCommandCorrectAsync(Command command);
    Task<bool> VerifyCommandSafetyAsync(Command command);
}