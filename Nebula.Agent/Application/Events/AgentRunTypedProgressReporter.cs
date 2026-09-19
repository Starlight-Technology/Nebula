using System.Text;
using Nebula.Agent.Application.Events;
using Nebula.Agent.Domain;
using Nebula.Core.Agent;
using Nebula.Core.Commands;
using Nebula.Core.Operations;
using Nebula.Core.Safety;

namespace Nebula.Agent.Application.Events;

internal sealed class AgentRunTypedProgressReporter : IProgress<ConversationTurn>
{
    private readonly IAgentRunEventPublisher _publisher;
    private readonly Guid _runId;
    private int _lastStep = 1;
    private int _lastAttempt = 1;

    public AgentRunTypedProgressReporter(IAgentRunEventPublisher publisher, Guid runId)
    {
        _publisher = publisher;
        _runId = runId;
    }

    public void Report(ConversationTurn turn)
    {
        var lastEvent = turn.ActionEvents.LastOrDefault();
        if (lastEvent != null)
        {
            _lastStep = lastEvent.Step;
            _lastAttempt = lastEvent.Attempt;
        }

        switch (turn.ActionStatus)
        {
            case ActionExecutionStatus.Started:
            case ActionExecutionStatus.Validating:
                _publisher.Publish(new AgentRunStartedEvent(_runId, DateTimeOffset.UtcNow,
                    turn.Prompt, turn.Mode, ""));
                break;

            case ActionExecutionStatus.Planning:
                if (!string.IsNullOrWhiteSpace(turn.Reasoning))
                {
                    _publisher.Publish(new AgentRunStepStartedEvent(_runId, DateTimeOffset.UtcNow,
                        _lastStep, _lastAttempt, turn.Prompt, OperationKind.Unknown, null));
                }
                if (!string.IsNullOrWhiteSpace(turn.CurrentPlan))
                {
                    var plan = ParsePlan(turn.CurrentPlan);
                    _publisher.Publish(new AgentRunPlanUpdatedEvent(_runId, DateTimeOffset.UtcNow, plan));
                }
                break;

            case ActionExecutionStatus.Executing:
                var cmdEvent = turn.ActionEvents.LastOrDefault(e =>
                    e.Kind == ActionExecutionEventKind.ActionStarted ||
                    e.Kind == ActionExecutionEventKind.StreamOutput);
                if (cmdEvent != null)
                {
                    var cmd = turn.Commands.FirstOrDefault(c => c.Id == _lastStep);
                    if (cmd != null)
                    {
                        if (cmdEvent.Kind == ActionExecutionEventKind.StreamOutput)
                        {
                            _publisher.Publish(new AgentRunCommandOutputEvent(_runId, DateTimeOffset.UtcNow,
                                _lastStep, _lastAttempt, cmdEvent.Message ?? "", cmdEvent.IsError));
                        }
                        else
                        {
                            _publisher.Publish(new AgentRunCommandExecutingEvent(_runId, DateTimeOffset.UtcNow,
                                _lastStep, _lastAttempt, cmd));
                        }
                    }
                }
                break;

            case ActionExecutionStatus.Observing:
                var obsEvent = turn.ActionEvents.LastOrDefault(e => e.Kind == ActionExecutionEventKind.Observation);
                if (obsEvent != null)
                {
                    var success = turn.ActionStatus != ActionExecutionStatus.Failed;
                    _publisher.Publish(new AgentRunStepCompletedEvent(_runId, DateTimeOffset.UtcNow,
                        _lastStep, _lastAttempt, obsEvent.Message ?? "", success));
                }
                break;

            case ActionExecutionStatus.AwaitingApproval:
                var approvalEvent = turn.ActionEvents.LastOrDefault(e => e.Kind == ActionExecutionEventKind.ApprovalRequired);
                if (approvalEvent != null)
                {
                    var cmd = turn.Commands.FirstOrDefault(c => c.Id == _lastStep);
                    if (cmd != null)
                    {
                        _publisher.Publish(new AgentRunApprovalRequiredEvent(_runId, DateTimeOffset.UtcNow,
                            _lastStep, _lastAttempt, cmd, approvalEvent.Message));
                    }
                }
                break;

            case ActionExecutionStatus.Retrying:
                var retryEvent = turn.ActionEvents.LastOrDefault(e => e.Kind == ActionExecutionEventKind.RetryScheduled);
                if (retryEvent != null)
                {
                    _publisher.Publish(new AgentRunStepRetryEvent(_runId, DateTimeOffset.UtcNow,
                        _lastStep, _lastAttempt, retryEvent.Message, _lastAttempt, 3));
                }
                break;

            case ActionExecutionStatus.Completed:
                _publisher.Publish(new AgentRunCompletedEvent(_runId, DateTimeOffset.UtcNow, turn));
                break;

            case ActionExecutionStatus.Failed:
            case ActionExecutionStatus.Unsafe:
            case ActionExecutionStatus.Blocked:
                var failEvent = turn.ActionEvents.LastOrDefault(e => e.IsError);
                _publisher.Publish(new AgentRunFailedEvent(_runId, DateTimeOffset.UtcNow,
                    failEvent?.Message ?? "Unknown failure", turn));
                break;

            case ActionExecutionStatus.Cancelled:
                _publisher.Publish(new AgentRunCancelledEvent(_runId, DateTimeOffset.UtcNow));
                break;
        }

        if (turn.IsDryRun && turn.ActionEvents.Any(e => e.Kind == ActionExecutionEventKind.ActionStarted))
        {
            _publisher.Publish(new AgentRunDryRunPreviewEvent(_runId, DateTimeOffset.UtcNow, turn.Commands));
        }
    }

    private static IReadOnlyList<AgentPlanStep> ParsePlan(string planText)
    {
        var steps = new List<AgentPlanStep>();
        var lines = planText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.StartsWith('#'))
            {
                continue;
            }

            try
            {
                var step = ParsePlanLine(trimmed);
                if (step != null)
                {
                    steps.Add(step);
                }
            }
            catch
            {
            }
        }

        return steps;
    }

    private static AgentPlanStep? ParsePlanLine(string line)
    {
        var idEnd = line.IndexOf(' ');
        if (idEnd <= 1)
        {
            return null;
        }

        if (!int.TryParse(line[1..idEnd], out var id))
        {
            return null;
        }

        var rest = line[(idEnd + 1)..].Trim();
        var status = "pending";
        var risk = "low";
        var isCheckpoint = false;
        var dependsOn = new List<int>();

        var statusStart = rest.IndexOf('[');
        while (statusStart >= 0)
        {
            var statusEnd = rest.IndexOf(']', statusStart);
            if (statusEnd < 0)
            {
                break;
            }

            var bracketContent = rest[(statusStart + 1)..statusEnd];

            if (bracketContent.StartsWith("risk=", StringComparison.OrdinalIgnoreCase))
            {
                risk = bracketContent[5..].Trim().ToLowerInvariant();
            }
            else if (bracketContent.Equals("checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                isCheckpoint = true;
            }
            else if (bracketContent.StartsWith("depends on", StringComparison.OrdinalIgnoreCase))
            {
                var depStr = bracketContent[10..].Trim();
                dependsOn = depStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var d) ? d : 0)
                    .Where(d => d > 0)
                    .ToList();
            }
            else if (bracketContent is "pending" or "inProgress" or "completed")
            {
                status = bracketContent;
            }

            rest = rest[(statusEnd + 1)..].Trim();
            statusStart = rest.IndexOf('[');
        }

        return new AgentPlanStep
        {
            Id = id,
            Description = rest,
            Status = status,
            Risk = risk,
            IsCheckpoint = isCheckpoint,
            DependsOn = dependsOn
        };
    }
}