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
        if (entity == null) return null;
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
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    public async Task<IEnumerable<StoredCommand>> GetByRequestIdAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var list = await context.Commands.Where(x => x.RequestId == requestId).ToListAsync(cancellationToken);
        return list.Select(entity => new StoredCommand
        {
            Id = entity.Id,
            RequestId = entity.RequestId,
            CommandId = entity.CommandId,
            Objective = entity.Objective,
            Command = entity.Command,
            OsType = entity.OsType,
            Executed = entity.Executed,
            ExecutionResult = entity.ExecutionResult,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        });
    }

    public async Task<StoredCommand> UpdateExecutionAsync(Guid commandId, bool executed, string? result, CancellationToken cancellationToken = default)
    {
        var entity = await context.Commands.FindAsync(new object[] { commandId }, cancellationToken);
        if (entity == null) throw new KeyNotFoundException("Command not found");
        entity.Executed = executed;
        entity.ExecutionResult = result;
        entity.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
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
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    public async Task<IEnumerable<StoredCommand>> GetByOsTypeAsync(string osType, CancellationToken cancellationToken = default)
    {
        var list = await context.Commands.Where(x => x.OsType == osType).ToListAsync(cancellationToken);
        return list.Select(entity => new StoredCommand
        {
            Id = entity.Id,
            RequestId = entity.RequestId,
            CommandId = entity.CommandId,
            Objective = entity.Objective,
            Command = entity.Command,
            OsType = entity.OsType,
            Executed = entity.Executed,
            ExecutionResult = entity.ExecutionResult,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        });
    }

    public async Task<IEnumerable<StoredCommand>> GetExecutedCommandsAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        var list = await context.Commands.Where(x => x.Executed).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return list.Select(entity => new StoredCommand
        {
            Id = entity.Id,
            RequestId = entity.RequestId,
            CommandId = entity.CommandId,
            Objective = entity.Objective,
            Command = entity.Command,
            OsType = entity.OsType,
            Executed = entity.Executed,
            ExecutionResult = entity.ExecutionResult,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        });
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