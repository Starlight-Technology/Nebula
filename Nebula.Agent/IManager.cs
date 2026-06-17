using Nebula.Core.Interactions;

namespace Nebula.Agent;

public interface IManager
{
    Guid ActiveConversationId { get; }

    Task<string> ManageResponse(UserMessage message);

    Task<ConversationTurn> ManageConversationAsync(UserMessage message);

    Task<ConversationTurn> ManageConversationAsync(
        UserMessage message,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default);

    Task<ConversationTurn> RunApprovedCommandAsync(
        CommandExecution command,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default);

    Guid StartNewConversation();

    Task<string> GenerateCommandSteps(string userRequest);

    Task<bool> VerifyCommandCorrectAsync(Command command);

    Task<bool> VerifyCommandSafetyAsync(Command command);
}
