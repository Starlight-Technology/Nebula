namespace Nebula.Agent.Data;

/// <summary>
/// Repository interface for persisting and querying StoredCommands in PostgreSQL.
/// Handles validated commands with their execution status and results.
/// Only commands that pass safety and correctness verification should be persisted.
/// </summary>
public interface ICommandRepository
{
    /// <summary>
    /// Saves a validated command to the database.
    /// </summary>
    Task<StoredCommand> SaveAsync(StoredCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a command by ID.
    /// </summary>
    Task<StoredCommand?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all commands for a specific request.
    /// </summary>
    Task<IEnumerable<StoredCommand>> GetByRequestIdAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a command's execution status and result.
    /// </summary>
    Task<StoredCommand> UpdateExecutionAsync(Guid commandId, bool executed, string? result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves commands by OS type (Windows, Linux, macOS).
    /// Useful for analyzing command distribution by platform.
    /// </summary>
    Task<IEnumerable<StoredCommand>> GetByOsTypeAsync(string osType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all executed commands with pagination.
    /// </summary>
    Task<IEnumerable<StoredCommand>> GetExecutedCommandsAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves command verification results.
    /// </summary>
    Task<CommandVerification> SaveVerificationAsync(CommandVerification verification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves verification results for a command.
    /// </summary>
    Task<CommandVerification?> GetVerificationAsync(Guid commandId, CancellationToken cancellationToken = default);
}
