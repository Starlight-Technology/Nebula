namespace Nebula.Agent;

public interface IManager
{
    Task<string> ManageResponse(string prompt);
}