using System.Text.RegularExpressions;

using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Llama.Client;
using Nebula.Runner;

namespace Nebula.Agent;

public class Manager : IManager
{
    private readonly ILlamaClient llamaClient;
    private readonly ILogger logger;
    private readonly IAgentActionRunner actionRunner;
    private readonly PromptClassifier promptClassifier;
    private readonly ChatResponseService chatResponseService;
    private readonly IConversationContextService conversationContextService;
    private readonly PromptRequestAuditService promptAuditService;
    private readonly int maxActionRetryCount;
    private readonly int maxActionStepCount;
    private Guid activeConversationId = Guid.NewGuid();

    public Manager(
        ILlamaClient llamaClient,
        IShellExecutor executor,
        IJsonExtractor jsonExtractor,
        ILogger logger,
        ICommandRepository? commandRepository = null,
        IPromptRequestRepository? promptRepository = null,
        IConversationMemoryRepository? conversationMemoryRepository = null,
        NebulaContextBuilder? contextBuilder = null,
        int maxActionRetries = 5,
        int maxActionSteps = AgentActionRunRequest.DefaultMaxSteps,
        IAgentActionRunner? actionRunner = null,
        IConversationContextService? conversationContextService = null)
    {
        this.llamaClient = llamaClient;
        this.logger = logger;
        this.actionRunner = actionRunner ?? new AgentActionRunner(
            llamaClient,
            executor,
            jsonExtractor,
            logger,
            commandRepository,
            maxActionRetries);

        var conversationContextBuilder = contextBuilder ?? new NebulaContextBuilder();
        promptClassifier = new PromptClassifier(llamaClient, logger);
        chatResponseService = new ChatResponseService(llamaClient);
        this.conversationContextService = conversationContextService
            ?? new ConversationContextService(
                conversationMemoryRepository,
                conversationContextBuilder,
                logger);
        promptAuditService = new PromptRequestAuditService(promptRepository, logger);
        maxActionRetryCount = Math.Max(0, maxActionRetries);
        maxActionStepCount = Math.Max(1, maxActionSteps);
    }

    public Guid ActiveConversationId => activeConversationId;

    public async Task<string> ManageResponse(string prompt)
    {
        var turn = await ManageConversationAsync(prompt);
        return turn.Response;
    }

    public Task<ConversationTurn> ManageConversationAsync(string prompt)
    {
        return ManageConversationAsync(prompt, progress: null, cancellationToken: default);
    }

    public Guid StartNewConversation()
    {
        activeConversationId = Guid.NewGuid();
        logger.Log($"Started new ConversationId '{activeConversationId}'.");
        return activeConversationId;
    }

    public async Task<ConversationTurn> ManageConversationAsync(
        string prompt,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return CreateEmptyPromptTurn(prompt);
        }

        var request = new ConversationRequest(
            activeConversationId,
            Guid.NewGuid(),
            prompt,
            llamaClient.SelectedModel);

        try
        {
            return await ProcessConversationAsync(request, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Log($"Request '{request.RequestId}' for ConversationId '{request.ConversationId}' was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError($"Error managing response: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> VerifyCommandCorrectAsync(Command command)
    {
        var response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". This following command {{command.Run}} execute EXACTLY {{command.Objective}} on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}? Command: {{command.Run}}
            """);

        return IsAffirmativeResponse(response);
    }

    public async Task<bool> VerifyCommandSafetyAsync(Command command)
    {
        var response = await llamaClient.GetResponseAsync(
            $$"""
            Response only with "Yes" or "No". Is the following command safe to execute on {{(OperatingSystem.IsWindows() ? "Windows" : "Linux")}}? Command: {{command.Run}}
            """);

        return IsAffirmativeResponse(response);
    }

    public async Task<string> GenerateCommandSteps(string userRequest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userRequest);

        var plannerPrompt = CommandPromptFactory.CreatePlanPrompt(userRequest, userRequest);
        return await llamaClient.GetResponseAsync(plannerPrompt);
    }

    private async Task<ConversationTurn> ProcessConversationAsync(
        ConversationRequest request,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken)
    {
        logger.Log(
            $"Using ConversationId '{request.ConversationId}' for request '{request.RequestId}'.");

        var conversationContext = await conversationContextService.PrepareAsync(
            request.ConversationId,
            request.Prompt,
            cancellationToken);
        var classification = await promptClassifier.ClassifyAsync(request.Prompt);
        var promptRequest = request.CreatePromptRequest(classification);

        await promptAuditService.SaveAsync(promptRequest, cancellationToken);

        var turn = await CreateTurnAsync(
            request,
            conversationContext.ModelPrompt,
            classification,
            progress,
            cancellationToken);

        await CompleteConversationAsync(
            request,
            conversationContext,
            promptRequest,
            turn,
            cancellationToken);

        return turn;
    }

    private Task<ConversationTurn> CreateTurnAsync(
        ConversationRequest request,
        string modelPrompt,
        ClassificationResult classification,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken)
    {
        return classification switch
        {
            ClassificationResult.Action => actionRunner.RunAsync(
                request.CreateActionRequest(
                    modelPrompt,
                    maxActionStepCount,
                    maxActionRetryCount),
                progress,
                cancellationToken),
            ClassificationResult.Chat => chatResponseService.GetResponseAsync(
                request,
                modelPrompt,
                progress,
                cancellationToken),
            _ => Task.FromResult(request.CreateUnknownClassificationTurn())
        };
    }

    private async Task CompleteConversationAsync(
        ConversationRequest request,
        PreparedConversationContext conversationContext,
        PromptRequest promptRequest,
        ConversationTurn turn,
        CancellationToken cancellationToken)
    {
        turn.ConversationId = request.ConversationId;
        promptRequest.Response = turn.Response;
        promptRequest.UpdatedAt = DateTime.UtcNow;

        await promptAuditService.UpdateResponseAsync(
            request.RequestId,
            turn.Response,
            cancellationToken);
        await conversationContextService.CompleteAsync(
            conversationContext,
            request.Prompt,
            turn,
            cancellationToken);
    }

    private ConversationTurn CreateEmptyPromptTurn(string prompt)
    {
        return new ConversationTurn
        {
            ConversationId = activeConversationId,
            RequestId = Guid.Empty,
            Prompt = prompt,
            ModelName = llamaClient.SelectedModel,
            Classification = ClassificationResult.Unknown.ToString(),
            Response = "The prompt are empty, write something."
        };
    }

    private static bool IsAffirmativeResponse(string rawResponse)
    {
        var response = ModelResponse.Parse(rawResponse).Response.Trim();
        return Regex.IsMatch(response, @"^yes\b", RegexOptions.IgnoreCase);
    }
}
