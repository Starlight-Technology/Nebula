using System.Text;

using Nebula.Agent.Application;
using Nebula.Core.Agent;
using Nebula.Core.Interactions;
using Nebula.Core.Memory;
using Nebula.Core.Operations;
using Nebula.Core.Projects;
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
        WorkspaceRoot = ReferenceWorkspace.Resolve(request.WorkspaceRoot);
        MaxSteps = Math.Max(1, request.MaxSteps ?? defaultMaxSteps);
        MaxRetriesPerStep = ResolveMaxRetries(request, defaultMaxRetriesPerStep);
        if (request.ConversationApprovedCommands is { Count: > 0 })
        {
            foreach (var command in request.ConversationApprovedCommands)
            {
                if (!string.IsNullOrWhiteSpace(command))
                {
                    ApprovedCommandsForConversation.Add(command);
                }
            }
        }

        if (request.ApprovedAction is { Scope: ApprovalScope.Conversation })
        {
            ApprovedCommandsForConversation.Add(
                CommandNormalization.Normalize(request.ApprovedAction.Command));
        }
    }

    public AgentActionRunRequest Request { get; }

    /// <summary>
    /// The reference workspace this run operates on. Always resolved to an
    /// existing folder (created when missing), defaulting to a fresh empty
    /// workspace when no root was specified.
    /// </summary>
    public ReferenceWorkspace WorkspaceRoot { get; }

    public DateTimeOffset RunStartedUtc { get; } = DateTimeOffset.UtcNow;

    public string? TranslatedObjective { get; set; }

    public List<ActionExecutionEvent> Events { get; } = [];

    public List<CommandExecution> Commands { get; } = [];

    public List<string> Observations { get; } = [];

    public List<string> CompletedPlanSteps { get; } = [];

    public List<string> PlanRevisions { get; } = [];

    public List<AgentPlanStep> Plan { get; } = [];

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

    public string CurrentPlan => BuildCurrentPlan();

    public List<ApprovalRecord> Approvals { get; } = [];

    public HashSet<string> ApprovedCommandsForConversation { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void RecordApproval(CommandExecution execution, bool autoApproved)
    {
        Approvals.Add(new ApprovalRecord(
            execution.StepId,
            execution.Objective,
            execution.Run,
            execution.SafetyDecision ?? CommandSafetyDecisionType.AskApproval,
            !autoApproved,
            autoApproved,
            DateTimeOffset.UtcNow));
    }

    public AgentActionDecisionRequest CreateDecisionRequest()
    {
        return new AgentActionDecisionRequest
        {
            Objective = TranslatedObjective ?? Request.Prompt,
            ChatHistoryContext = Request.ChatHistoryContext,
            CurrentPlan = BuildCurrentPlan(),
            PreviousActionResult = PreviousActionResult,
            Observations = Observations.ToList(),
            ExecutionHistory = ExecutionHistory.Entries.ToList(),
            StepNumber = StepNumber,
            RetryNumber = RetryNumber,
            WorkspaceRoot = WorkspaceRoot.Root
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
            WorkingDirectory = ResolveWorkingDirectory(
                action.WorkingDirectory,
                WorkspaceRoot.Root)
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

public void EmitApprovalGranted(CommandExecution execution)
    {
        var mode = execution.AutoApproved
            ? "automaticamente pelas preferencias do runtime"
            : "manualmente pela interface";
        Emit(
            ActionExecutionEventKind.ApprovalGranted,
            ActionExecutionStatus.Executing,
            "Approval granted",
            $"Comando aprovado {mode}.",
            command: execution.Run);
    }

    public void EmitStreamOutput(string chunk, bool isError, string? command = null)
    {
        const int maxStreamEventLength = 16_000;
        var last = Events.Count > 0 ? Events[^1] : null;
        if (last?.Kind == ActionExecutionEventKind.StreamOutput
            && last.Command == command
            && last.IsError == isError
            && (last.ToolResponse?.Length ?? 0) < maxStreamEventLength)
        {
            var combined = isError
                ? $"{last.Message}\nstderr: {chunk}"
                : last.ToolResponse is null or { Length: 0 }
                    ? chunk
                    : $"{last.ToolResponse}\n{chunk}";
            last.ToolResponse = SecretRedaction.Apply(combined);
            last.Message = isError ? $"stderr: {chunk}" : last.Message;
            last.CreatedAt = DateTime.UtcNow;
            progress?.Report(BuildTurn(last.Status, last.Message, false));
            return;
        }

        Emit(
            ActionExecutionEventKind.StreamOutput,
            ActionExecutionStatus.Executing,
            "Stream output",
            isError ? $"stderr: {chunk}" : chunk,
            command: command,
            toolResponse: chunk,
            isError: isError);
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
            ActionExecutionStatus.Observing,
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
            ActionExecutionStatus.Observing,
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
            ActionExecutionStatus.Blocked,
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
                ActionExecutionStatus.Correcting,
                "Error reflection",
                $"Likely cause: {reflection.Hypothesis}",
                command: reflection.NextCommand);
        }

        Emit(
            ActionExecutionEventKind.PlanRevised,
            ActionExecutionStatus.Correcting,
            "Plan revised",
            $"Marked the failed action as Failed and added an alternative: {alternative}");
    }

    public void CompleteStep(string objective, string observation)
    {
        CompletedPlanSteps.Add(
            $"{StepNumber}. {objective} - completed. Observation: " +
            TextTruncation.Truncate(observation, 500));
        MarkPlanStepCompleted(objective);
        StepNumber++;
        RetryNumber = 0;
    }

    public void ApplyPlan(IReadOnlyList<AgentPlanStep>? plan)
    {
        if (plan is null)
        {
            return;
        }

        foreach (var incoming in plan)
        {
            var existing = Plan.FirstOrDefault(step => step.Id == incoming.Id);
            if (existing is null)
            {
                Plan.Add(new AgentPlanStep
                {
                    Id = incoming.Id,
                    Description = incoming.Description,
                    DependsOn = incoming.DependsOn,
                    Status = incoming.Status
                });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(incoming.Description))
            {
                existing.Description = incoming.Description;
            }

            if (existing.Status == "completed")
            {
                continue;
            }

            existing.DependsOn = incoming.DependsOn;
            existing.Status = incoming.Status;
        }
    }

    private void MarkPlanStepCompleted(string objective)
    {
        var match = Plan.FirstOrDefault(step =>
            step.Status != "completed" &&
            string.Equals(
                step.Description.Trim(),
                objective.Trim(),
                StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            match.Status = "completed";
        }
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
            ActionExecutionStatus.Correcting,
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
        string? error = null,
        bool isError = false)
    {
        var actionEvent = new ActionExecutionEvent
        {
            Kind = kind,
            Status = status,
            Step = Math.Max(1, StepNumber),
            Attempt = Math.Max(1, AttemptNumber),
            Title = title,
            Message = SecretRedaction.Apply(message) ?? string.Empty,
            Command = SecretRedaction.Apply(command),
            ToolResponse = SecretRedaction.Apply(toolResponse),
            Error = SecretRedaction.Apply(error),
            IsError = isError
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
            Response = SecretRedaction.Apply(response) ?? string.Empty,
            Reasoning = BuildVisibleReasoning(),
            Commands = Commands.Select(MaskForUi).ToList(),
            ExecutionHistory = ExecutionHistory.Entries
                .Select(MaskForUi)
                .ToList(),
            Evidence = Evidence.Select(MaskForUi).ToList(),
            ActionStatus = status,
            ActionEvents = Events.Select(MaskForUi).ToList(),
            CurrentPlan = SecretRedaction.Apply(BuildCurrentPlan()) ?? string.Empty,
            Artifacts = BuildArtifactRecords(),
            Approvals = BuildApprovalRecords(),
            FinalReport = BuildFinalReport(status),
            IsCancelled = isCancelled
        };
    }

    public ConversationTurn Snapshot(
        ActionExecutionStatus status,
        string response = "Execucao em andamento.")
    {
        return BuildTurn(status, response, isCancelled: false);
    }

    private List<AgentArtifactRecord> BuildArtifactRecords()
    {
        var runId = Request.RequestId;
        return CreatedArtifacts.Values
            .Select(artifact => new AgentArtifactRecord(
                Guid.NewGuid(),
                runId,
                Path.GetFileName(artifact.Path) ?? artifact.Path,
                artifact.Path,
                artifact.ContentHash,
                DateTimeOffset.UtcNow))
            .ToList();
    }

    private List<AgentApprovalRecord> BuildApprovalRecords()
    {
        var runId = Request.RequestId;
        return Approvals
            .Select(approval => new AgentApprovalRecord(
                Guid.NewGuid(),
                runId,
                approval.StepId,
                approval.Objective,
                approval.Command,
                approval.Decision,
                approval.ApprovedByUser,
                approval.AutoApproved,
                approval.CreatedAt))
            .ToList();
    }

    private string? BuildFinalReport(ActionExecutionStatus status)
    {
        if (status is not (ActionExecutionStatus.Completed or ActionExecutionStatus.Failed
            or ActionExecutionStatus.Cancelled or ActionExecutionStatus.Unsafe))
        {
            return null;
        }

        var report = new StringBuilder();
        report.AppendLine("# Relatorio final");
        report.AppendLine();

        var changedFiles = Evidence
            .Where(value => value.Success &&
                value.OperationKind is OperationKind.FileWrite or OperationKind.ScriptContent)
            .Select(value => value.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        report.AppendLine($"## Arquivos alterados ({changedFiles.Count})");
        report.AppendLine(changedFiles.Count == 0
            ? "Nenhum arquivo foi alterado."
            : string.Join(Environment.NewLine, changedFiles.Select(path => $"- `{path}`")));
        report.AppendLine();

        var executed = Commands
            .Where(command => command.Executed)
            .ToList();
        report.AppendLine($"## Comandos executados ({executed.Count})");
        report.AppendLine(executed.Count == 0
            ? "Nenhum comando foi executado."
            : string.Join(Environment.NewLine, executed.Select(command =>
                $"- {command.OperationKind} `{command.Run}` (exit {command.ExitCode?.ToString() ?? "n/a"})")));
        report.AppendLine();

        var tests = executed
            .Where(command =>
                command.Run.Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToList();
        report.AppendLine($"## Testes rodados ({tests.Count})");
        report.AppendLine(tests.Count == 0
            ? "Nenhum teste foi executado."
            : string.Join(Environment.NewLine, tests.Select(command =>
                $"- `{command.Run}` (exit {command.ExitCode?.ToString() ?? "n/a"})")));
        report.AppendLine();

        var failures = executed
            .Where(command => command.ExitCode is not 0 && command.ExitCode is not null)
            .ToList();
        var riskyEvidence = Evidence
            .Where(value => !value.Success)
            .ToList();
        report.AppendLine("## Riscos e pendentes");
        if (failures.Count == 0 && riskyEvidence.Count == 0 && status != ActionExecutionStatus.Failed)
        {
            report.AppendLine("Nenhum risco identificado no fluxo.");
        }
        else
        {
            if (failures.Count > 0)
            {
                report.AppendLine("### Comandos com codigo de saida diferente de zero");
                foreach (var failure in failures)
                {
                    report.AppendLine(
                        $"- `{failure.Run}` (exit {failure.ExitCode}) — " +
                        $"{TextTruncation.Truncate(failure.StandardError, 200)}");
                }
            }

            if (riskyEvidence.Count > 0)
            {
                report.AppendLine("### Evidencias sem sucesso confirmado");
                foreach (var evidence in riskyEvidence)
                {
                    report.AppendLine(
                        $"- {evidence.OperationKind}: {TextTruncation.Truncate(evidence.Command ?? evidence.FilePath ?? "?", 200)}");
                }
            }

            if (status == ActionExecutionStatus.Failed)
            {
                report.AppendLine("### Resultado");
                report.AppendLine("- A tarefa NAO foi concluida com sucesso; ha riscos restantes.");
            }
        }

        return report.ToString();
    }

    private static CommandExecution MaskForUi(CommandExecution execution)
    {
        return new CommandExecution
        {
            StepId = execution.StepId,
            OperationKind = execution.OperationKind,
            Attempt = execution.Attempt,
            Id = execution.Id,
            Objective = execution.Objective,
            Run = SecretRedaction.Apply(execution.Run) ?? string.Empty,
            OriginalCommand = SecretRedaction.Apply(execution.OriginalCommand) ?? string.Empty,
            ResolvedFileName = execution.ResolvedFileName,
            ResolvedArguments = SecretRedaction.Apply(execution.ResolvedArguments) ?? string.Empty,
            OperatingSystem = execution.OperatingSystem,
            Shell = execution.Shell,
            ResolutionReasons = execution.ResolutionReasons,
            WorkingDirectory = execution.WorkingDirectory,
            TargetPath = execution.TargetPath,
            PlannedFiles = execution.PlannedFiles,
            ContentHash = execution.ContentHash,
            ClassificationSource = execution.ClassificationSource,
            ClassificationConfidence = execution.ClassificationConfidence,
            SafetyDecision = execution.SafetyDecision,
            ApprovedByUser = execution.ApprovedByUser,
            AutoApproved = execution.AutoApproved,
            Required = execution.Required,
            IsCorrect = execution.IsCorrect,
            IsSafe = execution.IsSafe,
            PassedLocalSafety = execution.PassedLocalSafety,
            Executed = execution.Executed,
            Skipped = execution.Skipped,
            StandardOutput = SecretRedaction.Apply(execution.StandardOutput) ?? string.Empty,
            StandardError = SecretRedaction.Apply(execution.StandardError) ?? string.Empty,
            ExitCode = execution.ExitCode,
            ExecutedAt = execution.ExecutedAt,
            Output = SecretRedaction.Apply(execution.Output),
            Notes = SecretRedaction.Apply(execution.Notes),
            Error = SecretRedaction.Apply(execution.Error),
            Sandboxed = execution.Sandboxed
        };
    }

    private static ExecutionHistoryEntry MaskForUi(ExecutionHistoryEntry entry)
    {
        return new ExecutionHistoryEntry
        {
            Command = SecretRedaction.Apply(entry.Command) ?? string.Empty,
            WorkingDirectory = entry.WorkingDirectory,
            StandardOutput = SecretRedaction.Apply(entry.StandardOutput) ?? string.Empty,
            StandardError = SecretRedaction.Apply(entry.StandardError) ?? string.Empty,
            ExitCode = entry.ExitCode,
            Success = entry.Success,
            Timestamp = entry.Timestamp,
            EnvironmentFingerprint = entry.EnvironmentFingerprint,
            FileFingerprint = entry.FileFingerprint,
            ErrorSignature = entry.ErrorSignature
        };
    }

    private static ExecutionEvidence MaskForUi(ExecutionEvidence evidence)
    {
        return evidence with
        {
            Command = SecretRedaction.Apply(evidence.Command),
            FilePath = evidence.FilePath,
            StdOut = SecretRedaction.Apply(evidence.StdOut),
            StdErr = SecretRedaction.Apply(evidence.StdErr)
        };
    }

    private static ActionExecutionEvent MaskForUi(ActionExecutionEvent actionEvent)
    {
        return new ActionExecutionEvent
        {
            Id = actionEvent.Id,
            CreatedAt = actionEvent.CreatedAt,
            Status = actionEvent.Status,
            Kind = actionEvent.Kind,
            Step = actionEvent.Step,
            Attempt = actionEvent.Attempt,
            Title = actionEvent.Title,
            Message = SecretRedaction.Apply(actionEvent.Message) ?? string.Empty,
            Command = SecretRedaction.Apply(actionEvent.Command),
            ToolResponse = SecretRedaction.Apply(actionEvent.ToolResponse),
            Error = SecretRedaction.Apply(actionEvent.Error),
            IsError = actionEvent.IsError
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
        if (Plan.Count > 0)
        {
            var planLines = Plan
                .OrderBy(step => step.Id)
                .Select(step =>
                {
                    var deps = step.DependsOn.Count == 0
                        ? string.Empty
                        : $" (depends on {string.Join(",", step.DependsOn)})";
                    return $"#{step.Id} [{step.Status}]{deps} {step.Description}";
                });
            return string.Join(Environment.NewLine, planLines);
        }

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

    internal static string ResolveWorkingDirectory(
        string? workingDirectory,
        string workspaceRoot)
    {
        return Path.GetFullPath(
            string.IsNullOrWhiteSpace(workingDirectory)
                ? workspaceRoot
                : workingDirectory);
    }
}

internal sealed record CreatedArtifact(
    string Path,
    string ContentHash,
    CommandClassification Classification);

internal sealed record ApprovalRecord(
    Guid StepId,
    string Objective,
    string Command,
    CommandSafetyDecisionType Decision,
    bool ApprovedByUser,
    bool AutoApproved,
    DateTimeOffset CreatedAt);
