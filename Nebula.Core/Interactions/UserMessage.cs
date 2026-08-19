namespace Nebula.Core.Interactions;

public enum InteractionMode
{
    Chat = 0,
    Agent = 1
}

public sealed record UserMessage(
    string Content,
    InteractionMode Mode,
    string? WorkspaceRoot = null,
    bool IsDryRun = false);
