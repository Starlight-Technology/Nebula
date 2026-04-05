namespace Nebula.Agent.Data;

public class CompositePromptRequestRepository(IEnumerable<IPromptRequestStore> stores) : IPromptRequestRepository
{
    private readonly IReadOnlyList<IPromptRequestStore> stores = stores.ToList();

    public async Task<PromptRequest> SaveAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        foreach (IPromptRequestStore store in stores)
        {
            await store.SaveAsync(request, cancellationToken);
        }

        return request;
    }

    public async Task<PromptRequest?> UpdateResponseAsync(Guid id, string response, CancellationToken cancellationToken = default)
    {
        PromptRequest? updatedRequest = null;

        foreach (IPromptRequestStore store in stores)
        {
            updatedRequest = await store.UpdateResponseAsync(id, response, cancellationToken) ?? updatedRequest;
        }

        return updatedRequest;
    }

    public async Task<PromptRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        foreach (IPromptRequestStore store in stores)
        {
            PromptRequest? request = await store.GetByIdAsync(id, cancellationToken);
            if (request != null)
            {
                return request;
            }
        }

        return null;
    }

    public async Task<IEnumerable<PromptRequest>> GetAllAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        var results = new List<PromptRequest>();

        foreach (IPromptRequestStore store in stores)
        {
            results.AddRange(await store.GetAllAsync(skip, take, cancellationToken));
        }

        return results
            .GroupBy(request => request.Id)
            .Select(group => group.First())
            .ToList();
    }

    public async Task<IEnumerable<PromptRequest>> GetByClassificationAsync(string classification, CancellationToken cancellationToken = default)
    {
        var results = new List<PromptRequest>();

        foreach (IPromptRequestStore store in stores)
        {
            results.AddRange(await store.GetByClassificationAsync(classification, cancellationToken));
        }

        return results
            .GroupBy(request => request.Id)
            .Select(group => group.First())
            .ToList();
    }
}
