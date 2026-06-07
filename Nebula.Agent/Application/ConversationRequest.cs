using Nebula.Agent.Data;
using Nebula.Llama.Client;

namespace Nebula.Agent.Application;

internal sealed record ConversationRequest(
    Guid ConversationId,
    Guid RequestId,
    string Prompt,
    string ModelName)
{
    public PromptRequest CreatePromptRequest(ClassificationResult classification)
    {
        return new PromptRequest
        {
            Id = RequestId,
            Prompt = Prompt,
            Classification = classification.ToString()
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
            ChatHistoryContext = chatHistoryContext,
            ModelName = ModelName,
            MaxSteps = maxSteps,
            MaxRetriesPerStep = maxRetriesPerStep
        };
    }

    public ConversationTurn CreateUnknownClassificationTurn()
    {
        return new ConversationTurn
        {
            ConversationId = ConversationId,
            RequestId = RequestId,
            Prompt = Prompt,
            ModelName = ModelName,
            Classification = ClassificationResult.Unknown.ToString(),
            Response = "Unable to classify the prompt. Please try again with a clearer request."
        };
    }
}
