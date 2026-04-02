namespace Nebula.Agent.Data;

/// <summary>
/// No-operation implementation of IPromptRequestRepository.
/// Used when database persistence is not configured.
/// </summary>
public class NoOpPromptRequestRepository : IPromptRequestRepository
{
    public Task<PromptRequest> SaveAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        request.CreatedAt = DateTime.UtcNow;
        request.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(request);
    }

    public Task<PromptRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<PromptRequest?>(null);
    }

    public Task<IEnumerable<PromptRequest>> GetAllAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<PromptRequest>>(new List<PromptRequest>());
    }

    public Task<IEnumerable<PromptRequest>> GetByClassificationAsync(string classification, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<PromptRequest>>(new List<PromptRequest>());
    }
}
