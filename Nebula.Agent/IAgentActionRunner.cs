namespace Nebula.Agent;

public interface IAgentActionRunner
{
    Task<ConversationTurn> RunAsync(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        CancellationToken cancellationToken = default);

    Task<AgentActionDecision> GenerateNextStepAsync(
        AgentActionDecisionRequest request,
        CancellationToken cancellationToken = default);

    [Obsolete("Use GenerateNextStepAsync for ReAct execution.")]
    Task<string> GeneratePlanAsync(
        string userRequest,
        string chatHistoryContext,
        IReadOnlyList<string>? previousFailures = null,
        CancellationToken cancellationToken = default);

    Task<ActionValidationResult> ValidateAsync(
        AgentActionRunRequest request,
        CancellationToken cancellationToken = default);
}
