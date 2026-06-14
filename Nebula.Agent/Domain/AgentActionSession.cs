using System.Text;

using Nebula.Agent.Application;
using Nebula.Core.Interactions;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Llama.Client;

namespace Nebula.Agent.Domain;

internal sealed class AgentActionSession
{
    private readonly IProgress<ConversationTurn>? progress;
    private readonly ILogger logger;
    private readonly string selectedModel;
    private AgentToolAction? pendingRecoveryAction;
    private string? pendingRecoveryReasoning;

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

    public List<string> PlanRevisions { get; } = [];

    public ExecutionHistory ExecutionHistory { get; } = new();

    public List<ExecutionEvidence> Evidence { get; } = [];

    public Dictionary<string, CreatedArtifact> CreatedArtifacts { get; } =
        new(StringComparer.OrdinalIgnoreCase);

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
            ExecutionHistory = ExecutionHistory.Entries.ToList(),
            StepNumber = StepNumber,
            RetryNumber = RetryNumber
        };
    }

    public bool TryTakeRecoveryDecision(out AgentActionDecision? decision)
    {
        if (pendingRecoveryAction is null)
        {
            decision = null;
            return false;
        }

        decision = new AgentActionDecision
        {
            ReasoningSummary = pendingRecoveryReasoning
                ?? "The failed step requires a different diagnostic action.",
            Action = pendingRecoveryAction
        };
        pendingRecoveryAction = null;
        pendingRecoveryReasoning = null;
        return true;
    }

    public AgentStep CreateStep(AgentToolAction action)
    {
        return new AgentStep
        {
            SessionId = Request.ConversationId,
            OriginalText = Request.Prompt,
            Objective = action.Objective,
            DeclaredKind = action.OperationKind,
            Command = action.Command,
            Content = action.Content,
            TargetPath = action.TargetPath,
            Language = action.Language,
            WorkingDirectory = ResolveWorkingDirectory(action.WorkingDirectory)
        };
    }

    public CommandExecution CreateExecution(
        AgentToolAction action,
        AgentStep step,
        OperationKind operationKind)
    {
        return new CommandExecution
        {
            StepId = step.Id,
            OperationKind = operationKind,
            Attempt = AttemptNumber,
            Id = StepNumber,
            Objective = action.Objective,
            Run = action.Command,
            OriginalCommand = action.Command,
            WorkingDirectory = step.WorkingDirectory,
            TargetPath = action.TargetPath,
            Required = true
        };
    }

    public void RecordEvidence(ExecutionEvidence evidence)
    {
        Evidence.Add(evidence);
        logger.Log(
            $"[AGENT] Evidence collected: sessionId={evidence.SessionId}; " +
            $"stepId={evidence.StepId}; operationKind={evidence.OperationKind}; " +
            $"evidenceId={evidence.Id}; executed={evidence.Executed}; " +
            $"exitCode={evidence.ExitCode?.ToString() ?? "(none)"}; success={evidence.Success}");
    }

    public void RecordArtifact(
        string path,
        string contentHash,
        CommandClassification classification)
    {
        CreatedArtifacts[Path.GetFullPath(path)] =
            new CreatedArtifact(path, contentHash, classification);
    }

    public bool TryGetArtifact(
        string path,
        out CreatedArtifact? artifact) =>
        CreatedArtifacts.TryGetValue(Path.GetFullPath(path), out artifact);

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

    public void EmitActionStarted(CommandExecution execution)
    {
        Emit(
            ActionExecutionEventKind.ActionStarted,
            ActionExecutionStatus.Executing,
            "Action started",
            execution.Objective,
            command: execution.Run);
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

    public void RecordExecution(ExecutionHistoryEntry entry)
    {
        ExecutionHistory.Add(entry);
    }

    public void RecordDeduplicationBlocked(
        CommandExecution execution,
        string reason)
    {
        execution.Skipped = true;
        execution.Error = reason;
        execution.Notes = reason;
        RecordObservation(execution.Run, reason);
        Emit(
            ActionExecutionEventKind.DeduplicationBlocked,
            ActionExecutionStatus.Retrying,
            "Repeated command blocked",
            reason,
            command: execution.Run,
            error: reason);
    }

    public void RevisePlan(
        CommandExecution execution,
        ErrorReflection? reflection)
    {
        var failureOutput = string.IsNullOrWhiteSpace(execution.StandardError)
            ? execution.StandardOutput
            : execution.StandardError;
        var failedStep =
            $"{StepNumber}.{AttemptNumber} {execution.Objective} - Failed. " +
            $"Exit code: {execution.ExitCode?.ToString() ?? "unknown"}. " +
            $"Error: {TextTruncation.Truncate(failureOutput, 500)}";
        PlanRevisions.Add(failedStep);

        var alternative = reflection is null
            ? "Choose a different command after inspecting stdout, stderr and exit code."
            : $"{reflection.AlternativeAction} - Pending. Command: {reflection.NextCommand}";
        PlanRevisions.Add($"{StepNumber}.{AttemptNumber + 1} Alternative - {alternative}");

        if (reflection is not null)
        {
            pendingRecoveryAction = new AgentToolAction
            {
                Objective = reflection.AlternativeAction,
                Command = reflection.NextCommand,
                WorkingDirectory = execution.WorkingDirectory,
                RequiresSafetyReview = true
            };
            pendingRecoveryReasoning =
                $"Likely cause: {reflection.Hypothesis} Trying a different diagnostic action.";
            RecordObservation(
                "error reflection",
                $"Hypothesis: {reflection.Hypothesis} Alternative: " +
                $"{reflection.AlternativeAction}. Next command: {reflection.NextCommand}");
            Emit(
                ActionExecutionEventKind.ErrorReflection,
                ActionExecutionStatus.Planning,
                "Error reflection",
                $"Likely cause: {reflection.Hypothesis}",
                command: reflection.NextCommand);
        }

        Emit(
            ActionExecutionEventKind.PlanRevised,
            ActionExecutionStatus.Planning,
            "Plan revised",
            $"Marked the failed action as Failed and added an alternative: {alternative}");
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

    public ConversationTurn RequestCommandApproval(CommandExecution execution)
    {
        var response =
            $"O passo {StepNumber} requer confirmação explícita antes da execução. {execution.Notes}";
        return Finish(
            ActionExecutionEventKind.ApprovalRequired,
            ActionExecutionStatus.AwaitingApproval,
            "Approval required",
            response,
            execution.Run);
    }

    public ConversationTurn BlockUnsupportedOperation(OperationKind operationKind)
    {
        var response =
            $"O passo {StepNumber} nao pode ser executado porque o tipo de operacao " +
            $"'{operationKind}' nao possui um executor seguro configurado.";
        return Finish(
            ActionExecutionEventKind.Unsafe,
            ActionExecutionStatus.Unsafe,
            "Unsupported operation",
            response);
    }

    public ConversationTurn Complete(string completionMessage)
    {
        if (Evidence.Count == 0)
        {
            return Finish(
                ActionExecutionEventKind.Failed,
                ActionExecutionStatus.Failed,
                "Insufficient evidence",
                "Nao ha evidencia suficiente para afirmar que a tarefa foi executada.");
        }

        var response = string.IsNullOrWhiteSpace(completionMessage)
            ? BuildEvidenceSummary()
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

    public ConversationTurn FailRepeatedError(
        ExecutionHistoryEntry failure,
        int failureCount)
    {
        var diagnostic = string.IsNullOrWhiteSpace(failure.StandardError)
            ? failure.StandardOutput
            : failure.StandardError;
        var response =
            $"A execucao automatica foi interrompida porque o mesmo erro ocorreu " +
            $"{failureCount} vezes. Diagnostico: {TextTruncation.Truncate(diagnostic, 1000)} " +
            "Revise permissoes, disponibilidade da ferramenta e o diretorio de trabalho " +
            "antes de continuar.";
        return Finish(
            ActionExecutionEventKind.Failed,
            ActionExecutionStatus.Failed,
            "Repeated error limit reached",
            response,
            failure.Command);
    }

    public ConversationTurn FailCommandNotFoundAlternative(
        ExecutionHistoryEntry originalFailure,
        ExecutionHistoryEntry alternativeFailure)
    {
        var diagnostic = string.IsNullOrWhiteSpace(alternativeFailure.StandardError)
            ? alternativeFailure.StandardOutput
            : alternativeFailure.StandardError;
        var response =
            $"A execucao foi interrompida depois que o comando '{originalFailure.Command}' " +
            "e uma alternativa compativel falharam. " +
            $"Diagnostico: {TextTruncation.Truncate(diagnostic, 1000)}";
        return Finish(
            ActionExecutionEventKind.Failed,
            ActionExecutionStatus.Failed,
            "Command not found alternative limit reached",
            response,
            alternativeFailure.Command);
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
            $"[AGENT] ReAct event [{kind}] step {actionEvent.Step} " +
            $"attempt {actionEvent.Attempt}: {message}");
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
            Mode = InteractionMode.Agent,
            ModelName = string.IsNullOrWhiteSpace(Request.ModelName)
                ? selectedModel
                : Request.ModelName,
            Classification = InteractionMode.Agent.ToString(),
            Response = response,
            Reasoning = BuildVisibleReasoning(),
            Commands = Commands.ToList(),
            ExecutionHistory = ExecutionHistory.Entries.ToList(),
            Evidence = Evidence.ToList(),
            ActionStatus = status,
            ActionEvents = Events.ToList(),
            IsCancelled = isCancelled
        };
    }

    private string BuildEvidenceSummary()
    {
        var successful = Evidence.Where(value => value.Success).ToList();
        if (successful.Count == 0)
        {
            return "Nao ha evidencia suficiente para afirmar sucesso.";
        }

        return string.Join(
            Environment.NewLine,
            successful.Select(value =>
            {
                var observed = !string.IsNullOrWhiteSpace(value.StdOut)
                    ? value.StdOut.Trim()
                    : value.FilePath ?? value.Command ?? "operation completed";
                return $"{value.OperationKind}: {observed}";
            }));
    }

    private string BuildCurrentPlan()
    {
        var planEntries = PlanRevisions.Concat(CompletedPlanSteps).ToList();
        return planEntries.Count == 0
            ? "No actions completed yet."
            : string.Join(Environment.NewLine, planEntries);
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

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        return Path.GetFullPath(
            string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory);
    }
}

internal sealed record CreatedArtifact(
    string Path,
    string ContentHash,
    CommandClassification Classification);
