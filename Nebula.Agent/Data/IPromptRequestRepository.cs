namespace Nebula.Agent.Data;

/// <summary>
/// Repository interface for persisting and querying PromptRequests in MongoDB.
/// Handles audit trail of user prompts and system classifications.
/// </summary>
public interface IPromptRequestRepository
{
    /// <summary>
    /// Saves a prompt request to the database.
    /// </summary>
    Task<PromptRequest> SaveAsync(PromptRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the final response for a stored prompt request.
    /// </summary>
    Task<PromptRequest?> UpdateResponseAsync(Guid id, string response, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a prompt request by ID.
    /// </summary>
    Task<PromptRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all prompt requests with pagination.
    /// </summary>
    Task<IEnumerable<PromptRequest>> GetAllAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves requests by classification type.
    /// </summary>
    Task<IEnumerable<PromptRequest>> GetByClassificationAsync(string classification, CancellationToken cancellationToken = default);
}
