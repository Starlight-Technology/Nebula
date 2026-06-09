using System.Text;

using Nebula.Agent.Application;
using Nebula.Llama.Client;

namespace Nebula.Agent.Domain;

internal sealed class AgentActionSession
{
    private readonly IProgress<ConversationTurn>? progress;
    private readonly ILogger logger;
    private readonly string selectedModel;

    public AgentActionSession(
        AgentActionRunRequest request,
        IProgress<ConversationTurn>? progress,
        ILogger logger,
        string selectedModel,
        int defaultMaxSteps,
        int defaultMaxRetriesPerStep)
    {
        Request = request;
        this.progress = progress;
        this.logger = logger;
        this.selectedModel = selectedModel;
        MaxSteps = Math.Max(1, request.MaxSteps ?? defaultMaxSteps);
        MaxRetriesPerStep = ResolveMaxRetries(request, defaultMaxRetriesPerStep);
    }

    public AgentActionRunRequest Request { get; }

    public List<ActionExecutionEvent> Events { get; } = [];

    public List<CommandExecution> Commands { get; } = [];

    public List<string> Observations { get; } = [];

    public List<string> CompletedPlanSteps { get; } = [];

    public int StepNumber { get; private set; } = 1;

    public int RetryNumber { get; private set; }

    public int AttemptNumber => RetryNumber + 1;

    public int MaxSteps { get; }

    public int MaxRetriesPerStep { get; }

    public bool StepLimitExceeded => StepNumber > MaxSteps;

    public string? PreviousActionResult { get; private set; }

    public AgentActionDecisionRequest CreateDecisionRequest()
    {
        return new AgentActionDecisionRequest
        {
            Objective = Request.Prompt,
            ChatHistoryContext = Request.ChatHistoryContext,
            CurrentPlan = BuildCurrentPlan(),
            PreviousActionResult = PreviousActionResult,
            Observations = Observations.ToList(),
            StepNumber = StepNumber,
            RetryNumber = RetryNumber
        };
    }

    public CommandExecution CreateExecution(AgentToolAction action)
    {
        return new CommandExecution
        {
            Attempt = AttemptNumber,
            Id = StepNumber,
            Objective = action.Objective,
            Run = action.Command,
            Required = true
        };
    }

    public void EmitValidationStarted()
    {
        Emit(
            ActionExecutionEventKind.ReasoningSummary,
            ActionExecutionStatus.Validating,
            "Reasoning summary",
            "I need to validate the objective before using local tools.");
    }

    public void EmitReasoning(string reasoningSummary)
    {
        Emit(
            ActionExecutionEventKind.ReasoningSummary,
            ActionExecutionStatus.Planning,
            "Reasoning summary",
            reasoningSummary);
    }

    public void EmitActionStarted(AgentToolAction action)
    {
        Emit(
            ActionExecutionEventKind.ActionStarted,
            ActionExecutionStatus.Executing,
            "Action started",
            action.Objective,
            command: action.Command);
    }

    public void EmitActionCompleted(CommandExecution execution)
    {
        Emit(
            ActionExecutionEventKind.ActionCompleted,
            ActionExecutionStatus.Executing,
            "Action completed",
            execution.Executed
                ? "The existing terminal tool completed the action."
                : "The existing terminal tool completed with a failure.",
            command: execution.Run,
            error: execution.Error);
    }

    public void EmitToolObservation(
        CommandExecution execution,
        string observation,
        string toolResponse)
    {
        Emit(
            ActionExecutionEventKind.Observation,
            ActionExecutionStatus.Executing,
            "Observation",
            observation,
            command: execution.Run,
            toolResponse: toolResponse,
            error: execution.Error);
    }

    public void RecordDecisionFailure(string failure, string error)
    {
        RecordObservation("decision", failure);
        Emit(
            ActionExecutionEventKind.ReasoningSummary,
            ActionExecutionStatus.Planning,
            "Reasoning summary",
            "I could not produce a valid next action, so I need to correct the decision.");
        Emit(
            ActionExecutionEventKind.Observation,
            ActionExecutionStatus.Planning,
            "Observation",
            failure,
            error: error);
    }

    public void RecordCommandObservation(string command, string observation)
    {
        RecordObservation(command, observation);
        Emit(
            ActionExecutionEventKind.Observation,
            ActionExecutionStatus.Executing,
            "Observation",
            observation,
            command: command);
    }

    public void RecordObservation(string action, string observation)
    {
        Observations.Add(BuildObservationRecord(action, observation));
        PreviousActionResult = observation;
    }

    public void CompleteStep(string objective, string observation)
    {
        CompletedPlanSteps.Add(
            $"{StepNumber}. {objective} - completed. Observation: " +
            TextTruncation.Truncate(observation, 500));
        StepNumber++;
        RetryNumber = 0;
    }

    public bool TryScheduleRetry(string failure)
    {
        if (RetryNumber >= MaxRetriesPerStep)
        {
            return false;
        }

        RetryNumber++;
        Emit(
            ActionExecutionEventKind.RetryScheduled,
            ActionExecutionStatus.Retrying,
            "Retry scheduled",
            $"Retry {RetryNumber} of {MaxRetriesPerStep}. Previous observation: {failure}");
        return true;
    }

    public ConversationTurn BlockUnsafeRequest(string reason)
    {
        var response = $"A acao foi bloqueada antes de executar ferramentas. Motivo: {reason}";
        return Finish(
            ActionExecutionEventKind.Unsafe,
            ActionExecutionStatus.Unsafe,
            "Unsafe",
            response);
    }

    public ConversationTurn BlockUnsafeCommand(CommandExecution execution)
    {
        var response =
            $"A acao foi interrompida porque o passo {StepNumber} ficou inseguro. {execution.Notes}";
        return Finish(
            ActionExecutionEventKind.Unsafe,
            ActionExecutionStatus.Unsafe,
            "Unsafe",
            response,
            execution.Run);
    }

    public ConversationTurn Complete(string completionMessage)
    {
        var response = string.IsNullOrWhiteSpace(completionMessage)
            ? "Objetivo concluido com sucesso."
            : completionMessage.Trim();
        return Finish(
            ActionExecutionEventKind.Completed,
            ActionExecutionStatus.Completed,
            "Completed",
            response);
    }

    public ConversationTurn FailStepLimit()
    {
        var response = $"Nao consegui concluir a acao antes do limite de {MaxSteps} passo(s).";
        return Finish(
            ActionExecutionEventKind.Failed,
            ActionExecutionStatus.Failed,
            "Failed",
            response);
    }

    public ConversationTurn FailRetryLimit(string failure)
    {
        var response =
            $"Nao consegui concluir o passo {StepNumber}. " +
            $"Limite de retry por passo ({MaxRetriesPerStep}) atingido. Motivo: {failure}";
        return Finish(
            ActionExecutionEventKind.Failed,
            ActionExecutionStatus.Failed,
            "Failed",
            response);
    }

    public ConversationTurn Cancel()
    {
        const string response = "Execucao cancelada pelo usuario.";
        return Finish(
            ActionExecutionEventKind.Cancelled,
            ActionExecutionStatus.Cancelled,
            "Cancelled",
            response,
            isCancelled: true);
    }

    public static string BuildObservationContext(IReadOnlyList<string> observations)
    {
        if (observations.Count == 0)
        {
            return "No observations yet.";
        }

        var observationContext = string.Join(Environment.NewLine, observations);
        return observationContext.Length <= 16000
            ? observationContext
            : observationContext[^16000..];
    }

    private ConversationTurn Finish(
        ActionExecutionEventKind kind,
        ActionExecutionStatus status,
        string title,
        string response,
        string? command = null,
        bool isCancelled = false)
    {
        Emit(kind, status, title, response, command);
        return BuildTurn(status, response, isCancelled);
    }

    private void Emit(
        ActionExecutionEventKind kind,
        ActionExecutionStatus status,
        string title,
        string message,
        string? command = null,
        string? toolResponse = null,
        string? error = null)
    {
        var actionEvent = new ActionExecutionEvent
        {
            Kind = kind,
            Status = status,
            Step = Math.Max(1, StepNumber),
            Attempt = Math.Max(1, AttemptNumber),
            Title = title,
            Message = message,
            Command = command,
            ToolResponse = toolResponse,
            Error = error
        };

        Events.Add(actionEvent);
        logger.Log(
            $"ReAct event [{kind}] step {actionEvent.Step} attempt {actionEvent.Attempt}: {message}");
        progress?.Report(BuildTurn(status, message, status == ActionExecutionStatus.Cancelled));
    }

    private ConversationTurn BuildTurn(
        ActionExecutionStatus status,
        string response,
        bool isCancelled)
    {
        return new ConversationTurn
        {
            ConversationId = Request.ConversationId,
            RequestId = Request.RequestId,
            Prompt = Request.Prompt,
            ModelName = string.IsNullOrWhiteSpace(Request.ModelName)
                ? selectedModel
                : Request.ModelName,
            Classification = status == ActionExecutionStatus.Cancelled
                ? ActionExecutionStatus.Cancelled.ToString()
                : ClassificationResult.Action.ToString(),
            Response = response,
            Reasoning = BuildVisibleReasoning(),
            Commands = Commands.ToList(),
            ActionStatus = status,
            ActionEvents = Events.ToList(),
            IsCancelled = isCancelled
        };
    }

    private string BuildCurrentPlan()
    {
        return CompletedPlanSteps.Count == 0
            ? "No actions completed yet."
            : string.Join(Environment.NewLine, CompletedPlanSteps);
    }

    private string BuildObservationRecord(string action, string observation)
    {
        return
            $"Step {StepNumber}, attempt {AttemptNumber}, action `{action}`: " +
            TextTruncation.Truncate(observation, 2000);
    }

    private string BuildVisibleReasoning()
    {
        var reasoning = new StringBuilder();
        foreach (var actionEvent in Events)
        {
            AppendEvent(reasoning, actionEvent);
        }

        return reasoning.ToString().Trim();
    }

    private static void AppendEvent(StringBuilder reasoning, ActionExecutionEvent actionEvent)
    {
        reasoning.AppendLine(
            $"[{actionEvent.Kind}] Step {actionEvent.Step}, attempt {actionEvent.Attempt}: " +
            actionEvent.Message);

        AppendEventValue(reasoning, "Action", actionEvent.Command);
        AppendEventValue(reasoning, "Observation", actionEvent.ToolResponse?.Trim());
        AppendEventValue(reasoning, "Error", actionEvent.Error);
    }

    private static void AppendEventValue(
        StringBuilder reasoning,
        string label,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            reasoning.AppendLine($"{label}: {value}");
        }
    }

    private static int ResolveMaxRetries(
        AgentActionRunRequest request,
        int defaultMaxRetriesPerStep)
    {
#pragma warning disable CS0618
        return Math.Max(
            0,
            request.MaxRetriesPerStep ??
            request.MaxRetries ??
            defaultMaxRetriesPerStep);
#pragma warning restore CS0618
    }
}
