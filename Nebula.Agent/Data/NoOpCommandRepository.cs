namespace Nebula.Agent.Data;

/// <summary>
/// No-operation implementation of ICommandRepository.
/// Used when database persistence is not configured.
/// </summary>
public class NoOpCommandRepository : ICommandRepository
{
    public Task<StoredCommand> SaveAsync(StoredCommand command, CancellationToken cancellationToken = default)
    {
        command.CreatedAt = DateTime.UtcNow;
        command.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(command);
    }

    public Task<StoredCommand?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<StoredCommand?>(null);
    }

    public Task<IEnumerable<StoredCommand>> GetByRequestIdAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<StoredCommand>>(new List<StoredCommand>());
    }

    public Task<StoredCommand> UpdateExecutionAsync(Guid commandId, bool executed, string? result, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new StoredCommand { Id = commandId, Executed = executed, ExecutionResult = result });
    }

    public Task<StoredCommand> UpdateExecutionDetailsAsync(
        Guid commandId,
        bool executed,
        string? result,
        int? exitCode,
        string? standardOutput,
        string? standardError,
        DateTimeOffset? executedAt,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new StoredCommand
        {
            Id = commandId,
            Executed = executed,
            ExecutionResult = result,
            ExitCode = exitCode,
            StandardOutput = standardOutput,
            StandardError = standardError,
            ExecutedAt = executedAt
        });
    }

    public Task<IEnumerable<StoredCommand>> GetApprovedCommandsAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<StoredCommand>>(new List<StoredCommand>());
    }

    public Task<IEnumerable<StoredCommand>> GetByOsTypeAsync(string osType, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<StoredCommand>>(new List<StoredCommand>());
    }

    public Task<IEnumerable<StoredCommand>> GetExecutedCommandsAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<StoredCommand>>(new List<StoredCommand>());
    }

    public Task<CommandVerification> SaveVerificationAsync(CommandVerification verification, CancellationToken cancellationToken = default)
    {
        verification.CreatedAt = DateTime.UtcNow;
        return Task.FromResult(verification);
    }

    public Task<CommandVerification?> GetVerificationAsync(Guid commandId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<CommandVerification?>(null);
    }
}
