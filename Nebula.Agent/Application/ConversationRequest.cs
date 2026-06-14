using Nebula.Agent.Data;
using Nebula.Core.Interactions;

namespace Nebula.Agent.Application;

internal sealed record ConversationRequest(
    Guid ConversationId,
    Guid RequestId,
    UserMessage Message,
    string ModelName)
{
    public string Prompt => Message.Content;

    public InteractionMode Mode => Message.Mode;

    public PromptRequest CreatePromptRequest()
    {
        return new PromptRequest
        {
            Id = RequestId,
            Prompt = Prompt,
            Mode = Mode,
            Classification = Mode.ToString()
        };
    }

    public AgentActionRunRequest CreateActionRequest(
        string chatHistoryContext,
        int maxSteps,
        int maxRetriesPerStep)
    {
        return new AgentActionRunRequest
        {
            ConversationId = ConversationId,
            RequestId = RequestId,
            Prompt = Prompt,
            Mode = InteractionMode.Agent,
            ChatHistoryContext = chatHistoryContext,
            ModelName = ModelName,
            MaxSteps = maxSteps,
            MaxRetriesPerStep = maxRetriesPerStep
        };
    }
}
