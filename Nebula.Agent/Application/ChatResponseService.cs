using Nebula.Llama.Client;

namespace Nebula.Agent.Application;

internal sealed class ChatResponseService(ILlamaClient llamaClient)
{
    public async Task<ConversationTurn> GetResponseAsync(
        ConversationRequest request,
        string modelPrompt,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken)
    {
        var streamingProgress = CreateStreamingProgress(request, progress);
        var rawResponse = progress is null
            ? await llamaClient.GetResponseAsync(modelPrompt)
            : await llamaClient.GetResponseAsync(modelPrompt, streamingProgress, cancellationToken);
        var parsedResponse = ModelResponse.Parse(rawResponse);

        return new ConversationTurn
        {
            ConversationId = request.ConversationId,
            RequestId = request.RequestId,
            Prompt = request.Prompt,
            ModelName = request.ModelName,
            Classification = ClassificationResult.Chat.ToString(),
            Response = string.IsNullOrWhiteSpace(parsedResponse.Response)
                ? "Nao consegui gerar uma resposta para esse pedido."
                : parsedResponse.Response,
            Reasoning = string.IsNullOrWhiteSpace(parsedResponse.Reasoning)
                ? null
                : parsedResponse.Reasoning
        };
    }

    private static IProgress<LlamaStreamUpdate>? CreateStreamingProgress(
        ConversationRequest request,
        IProgress<ConversationTurn>? progress)
    {
        if (progress is null)
        {
            return null;
        }

        return new InlineProgress<LlamaStreamUpdate>(
            update => progress.Report(CreateStreamingTurn(request, update)));
    }

    private static ConversationTurn CreateStreamingTurn(
        ConversationRequest request,
        LlamaStreamUpdate update)
    {
        return new ConversationTurn
        {
            ConversationId = request.ConversationId,
            RequestId = request.RequestId,
            Prompt = request.Prompt,
            ModelName = request.ModelName,
            Classification = ClassificationResult.Chat.ToString(),
            Response = update.Response,
            Reasoning = string.IsNullOrWhiteSpace(update.Reasoning) ? null : update.Reasoning
        };
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
