namespace Nebula.Agent;

public interface IManager
{
    Guid ActiveConversationId { get; }

    Task<string> ManageResponse(string prompt);
    Task<ConversationTurn> ManageConversationAsync(string prompt);
    Task<ConversationTurn> ManageConversationAsync(
        string prompt,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default);
    Guid StartNewConversation();
    Task<string> GenerateCommandSteps(string userRequest);
    Task<bool> VerifyCommandCorrectAsync(Command command);
    Task<bool> VerifyCommandSafetyAsync(Command command);
}
