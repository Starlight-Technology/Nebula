using Microsoft.EntityFrameworkCore;

using Nebula.Core.Agent;
using Nebula.Core.Operations;
using Nebula.Core.Safety;

using AgentRunEntity = Nebula.Postgres.Context.Entities.AgentRunEntity;
using AgentStepRecordEntity = Nebula.Postgres.Context.Entities.AgentStepRecordEntity;
using AgentArtifactEntity = Nebula.Postgres.Context.Entities.AgentArtifactEntity;
using AgentApprovalEntity = Nebula.Postgres.Context.Entities.AgentApprovalEntity;

namespace Nebula.Postgres.Context;

public sealed class PostgresAgentRunStore(PostgresContext context) : IAgentRunStore
{
    public async Task SaveRunAsync(
        AgentRun run,
        CancellationToken cancellationToken = default)
    {
        var entity = Map(run);
        var existing = await context.AgentRuns
            .Include(value => value.Steps)
            .Include(value => value.Artifacts)
            .Include(value => value.Approvals)
            .SingleOrDefaultAsync(value => value.Id == run.Id, cancellationToken);
        if (existing is null)
        {
            context.AgentRuns.Add(entity);
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        existing.Status = entity.Status;
        existing.FinishedAt = entity.FinishedAt;
        existing.Response = entity.Response;
        existing.IsCancelled = entity.IsCancelled;
        existing.CurrentPlan = entity.CurrentPlan;
        existing.WorkspaceRoot = entity.WorkspaceRoot;

        context.AgentStepRecords.RemoveRange(existing.Steps.ToList());
        context.AgentArtifacts.RemoveRange(existing.Artifacts.ToList());
        context.AgentApprovals.RemoveRange(existing.Approvals.ToList());
        await context.SaveChangesAsync(cancellationToken);

        foreach (var step in entity.Steps)
        {
            step.RunId = run.Id;
            context.AgentStepRecords.Add(step);
        }

        foreach (var artifact in entity.Artifacts)
        {
            artifact.RunId = run.Id;
            context.AgentArtifacts.Add(artifact);
        }

        foreach (var approval in entity.Approvals)
        {
            approval.RunId = run.Id;
            context.AgentApprovals.Add(approval);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentRun>> GetRunsAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var entities = await context.AgentRuns
            .Include(value => value.Steps)
            .Include(value => value.Artifacts)
            .Include(value => value.Approvals)
            .OrderByDescending(value => value.StartedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
        return entities
            .Select(Map)
            .ToList();
    }

    public async Task<AgentRun?> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var entity = await context.AgentRuns
            .Include(value => value.Steps)
            .Include(value => value.Artifacts)
            .Include(value => value.Approvals)
            .SingleOrDefaultAsync(value => value.Id == runId, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<AgentRun>> GetUnfinishedRunsAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var entities = await context.AgentRuns
            .Include(value => value.Steps)
            .Include(value => value.Artifacts)
            .Include(value => value.Approvals)
            .Where(value => value.ConversationId == conversationId &&
                            value.FinishedAt == null)
            .OrderByDescending(value => value.StartedAt)
            .ToListAsync(cancellationToken);
        return entities
            .Select(Map)
            .ToList();
    }

    private static AgentRunEntity Map(AgentRun run)
    {
        return new AgentRunEntity
        {
            Id = run.Id,
            ConversationId = run.ConversationId,
            RequestId = run.RequestId,
            Prompt = run.Prompt,
            ModelName = run.ModelName,
            Status = run.Status,
            StartedAt = run.StartedAt,
            FinishedAt = run.FinishedAt,
            Response = run.Response,
            IsCancelled = run.IsCancelled,
            CurrentPlan = run.CurrentPlan,
            WorkspaceRoot = run.WorkspaceRoot,
            Steps = run.Steps.Select(Map).ToList(),
            Artifacts = run.Artifacts?.Select(Map).ToList() ?? [],
            Approvals = run.Approvals?.Select(Map).ToList() ?? []
        };
    }

    private static AgentStepRecordEntity Map(AgentStepRecord step)
    {
        return new AgentStepRecordEntity
        {
            Id = step.Id,
            RunId = step.RunId,
            Step = step.Step,
            Attempt = step.Attempt,
            OperationKind = step.OperationKind.ToString(),
            Objective = step.Objective,
            Command = step.Command,
            WorkingDirectory = step.WorkingDirectory,
            TargetPath = step.TargetPath,
            ExitCode = step.ExitCode,
            Success = step.Success,
            CreatedAt = step.CreatedAt,
            StandardOutput = step.StandardOutput,
            StandardError = step.StandardError,
            Shell = step.Shell,
            SafetyDecision = step.SafetyDecision?.ToString(),
            ApprovedByUser = step.ApprovedByUser,
            AutoApproved = step.AutoApproved
        };
    }

    private static AgentArtifactEntity Map(AgentArtifactRecord artifact)
    {
        return new AgentArtifactEntity
        {
            Id = artifact.Id,
            RunId = artifact.RunId,
            Name = artifact.Name,
            Path = artifact.Path,
            ContentHash = artifact.ContentHash,
            CreatedAt = artifact.CreatedAt
        };
    }

    private static AgentApprovalEntity Map(AgentApprovalRecord approval)
    {
        return new AgentApprovalEntity
        {
            Id = approval.Id,
            RunId = approval.RunId,
            StepId = approval.StepId,
            Objective = approval.Objective,
            Command = approval.Command,
            Decision = approval.Decision.ToString(),
            ApprovedByUser = approval.ApprovedByUser,
            AutoApproved = approval.AutoApproved,
            CreatedAt = approval.CreatedAt
        };
    }

    private static AgentRun Map(AgentRunEntity entity)
    {
        return new AgentRun(
            entity.Id,
            entity.ConversationId,
            entity.RequestId,
            entity.Prompt,
            entity.ModelName,
            entity.Status,
            entity.StartedAt,
            entity.FinishedAt,
            entity.Response,
            entity.IsCancelled,
            entity.Steps
                .OrderBy(step => step.Step)
                .ThenBy(step => step.Attempt)
                .Select(step => new AgentStepRecord(
                    step.Id,
                    step.RunId,
                    step.Step,
                    step.Attempt,
                    Enum.TryParse<OperationKind>(step.OperationKind, out var kind)
                        ? kind
                        : OperationKind.Unknown,
                    step.Objective,
                    step.Command,
                    step.WorkingDirectory,
                    step.TargetPath,
                    step.ExitCode,
                    step.Success,
                    step.CreatedAt,
                    step.StandardOutput,
                    step.StandardError,
                    step.Shell,
                    Enum.TryParse<CommandSafetyDecisionType>(
                        step.SafetyDecision, out var safetyDecision)
                        ? safetyDecision
                        : (CommandSafetyDecisionType?)null,
                    step.ApprovedByUser,
                    step.AutoApproved))
                .ToList(),
            entity.CurrentPlan,
            entity.Artifacts
                .Select(artifact => new AgentArtifactRecord(
                    artifact.Id,
                    artifact.RunId,
                    artifact.Name,
                    artifact.Path,
                    artifact.ContentHash,
                    artifact.CreatedAt))
                .ToList(),
            entity.Approvals
                .Select(approval => new AgentApprovalRecord(
                    approval.Id,
                    approval.RunId,
                    approval.StepId,
                    approval.Objective,
                    approval.Command,
                    Enum.TryParse<CommandSafetyDecisionType>(
                        approval.Decision, out var approvalDecision)
                        ? approvalDecision
                        : CommandSafetyDecisionType.AskApproval,
                    approval.ApprovedByUser,
                    approval.AutoApproved,
                    approval.CreatedAt))
                .ToList(),
            entity.WorkspaceRoot);
    }
}
