using Microsoft.EntityFrameworkCore;
using Nebula.Postgres.Context;
using Nebula.Postgres.Context.Entities;

namespace Nebula.Agent.Data;

public class PostgresCommandRepository : ICommandRepository
{
    private readonly PostgresContext context;

    public PostgresCommandRepository(PostgresContext context)
    {
        this.context = context;
    }

    public async Task<StoredCommand> SaveAsync(StoredCommand command, CancellationToken cancellationToken = default)
    {
        var entity = new Nebula.Postgres.Context.Entities.StoredCommand
        {
            Id = command.Id,
            RequestId = command.RequestId,
            CommandId = command.CommandId,
            Objective = command.Objective,
            Command = command.Command,
            OsType = command.OsType,
            Executed = command.Executed,
            ExecutionResult = command.ExecutionResult,
            WorkingDirectory = command.WorkingDirectory,
            Shell = command.Shell,
            ExitCode = command.ExitCode,
            StandardOutput = command.StandardOutput,
            StandardError = command.StandardError,
            SafetyDecision = command.SafetyDecision,
            ApprovedByUser = command.ApprovedByUser,
            AutoApproved = command.AutoApproved,
            Skipped = command.Skipped,
            Required = command.Required,
            ExecutedAt = command.ExecutedAt,
            CreatedAt = command.CreatedAt,
            UpdatedAt = command.UpdatedAt
        };

        context.Commands.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        command.Id = entity.Id;
        return command;
    }

    public async Task<StoredCommand?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await context.Commands.FindAsync(new object[] { id }, cancellationToken);
        return entity == null ? null : Map(entity);
    }

    public async Task<IEnumerable<StoredCommand>> GetByRequestIdAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var list = await context.Commands.Where(x => x.RequestId == requestId).ToListAsync(cancellationToken);
        return list.Select(Map);
    }

    public async Task<StoredCommand> UpdateExecutionAsync(Guid commandId, bool executed, string? result, CancellationToken cancellationToken = default)
    {
        var entity = await context.Commands.FindAsync(new object[] { commandId }, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Command not found");
        entity.Executed = executed;
        entity.ExecutionResult = result;
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<StoredCommand> UpdateExecutionDetailsAsync(
        Guid commandId,
        bool executed,
        string? result,
        int? exitCode,
        string? standardOutput,
        string? standardError,
        DateTimeOffset? executedAt,
        CancellationToken cancellationToken = default)
    {
        var entity = await context.Commands.FindAsync(new object[] { commandId }, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Command not found");
        entity.Executed = executed;
        entity.ExecutionResult = result;
        entity.ExitCode = exitCode;
        entity.StandardOutput = standardOutput;
        entity.StandardError = standardError;
        entity.ExecutedAt = executedAt;
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<IEnumerable<StoredCommand>> GetApprovedCommandsAsync(
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var list = await context.Commands
            .Where(x => x.ApprovedByUser || x.AutoApproved)
            .OrderByDescending(x => x.ExecutedAt ?? x.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return list.Select(Map);
    }

    public async Task<IEnumerable<StoredCommand>> GetByOsTypeAsync(string osType, CancellationToken cancellationToken = default)
    {
        var list = await context.Commands.Where(x => x.OsType == osType).ToListAsync(cancellationToken);
        return list.Select(Map);
    }

    public async Task<IEnumerable<StoredCommand>> GetExecutedCommandsAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        var list = await context.Commands.Where(x => x.Executed).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return list.Select(Map);
    }

    private static StoredCommand Map(Nebula.Postgres.Context.Entities.StoredCommand entity)
    {
        return new StoredCommand
        {
            Id = entity.Id,
            RequestId = entity.RequestId,
            CommandId = entity.CommandId,
            Objective = entity.Objective,
            Command = entity.Command,
            OsType = entity.OsType,
            Executed = entity.Executed,
            ExecutionResult = entity.ExecutionResult,
            WorkingDirectory = entity.WorkingDirectory,
            Shell = entity.Shell,
            ExitCode = entity.ExitCode,
            StandardOutput = entity.StandardOutput,
            StandardError = entity.StandardError,
            SafetyDecision = entity.SafetyDecision,
            ApprovedByUser = entity.ApprovedByUser,
            AutoApproved = entity.AutoApproved,
            Skipped = entity.Skipped,
            Required = entity.Required,
            ExecutedAt = entity.ExecutedAt,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    public async Task<CommandVerification> SaveVerificationAsync(CommandVerification verification, CancellationToken cancellationToken = default)
    {
        var entity = new Nebula.Postgres.Context.Entities.CommandVerification
        {
            Id = verification.Id,
            CommandId = verification.CommandId,
            IsCorrect = verification.IsCorrect,
            IsSafe = verification.IsSafe,
            VerificationNotes = verification.VerificationNotes,
            CreatedAt = verification.CreatedAt
        };

        context.CommandVerifications.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        verification.Id = entity.Id;
        return verification;
    }

    public async Task<CommandVerification?> GetVerificationAsync(Guid commandId, CancellationToken cancellationToken = default)
    {
        var entity = await context.CommandVerifications.FirstOrDefaultAsync(x => x.CommandId == commandId, cancellationToken);
        if (entity == null) return null;
        return new CommandVerification
        {
            Id = entity.Id,
            CommandId = entity.CommandId,
            IsCorrect = entity.IsCorrect,
            IsSafe = entity.IsSafe,
            VerificationNotes = entity.VerificationNotes,
            CreatedAt = entity.CreatedAt
        };
    }
}