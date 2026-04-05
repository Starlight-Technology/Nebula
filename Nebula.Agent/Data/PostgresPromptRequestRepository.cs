using Microsoft.EntityFrameworkCore;
using Nebula.Postgres.Context;
using Nebula.Postgres.Context.Entities;

namespace Nebula.Agent.Data;

public class PostgresPromptRequestRepository(PostgresContext context) : IPromptRequestStore
{
    public async Task<PromptRequest> SaveAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        var entity = new Request
        {
            Id = request.Id,
            Prompt = request.Prompt,
            Classification = request.Classification,
            Response = request.Response,
            CreatedAt = request.CreatedAt,
            UpdatedAt = request.UpdatedAt
        };

        context.Requests.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        return request;
    }

    public async Task<PromptRequest?> UpdateResponseAsync(Guid id, string response, CancellationToken cancellationToken = default)
    {
        var entity = await context.Requests.FirstOrDefaultAsync(request => request.Id == id, cancellationToken);
        if (entity == null)
        {
            return null;
        }

        entity.Response = response;
        entity.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    public async Task<PromptRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await context.Requests
            .AsNoTracking()
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken);

        return entity == null ? null : Map(entity);
    }

    public async Task<IEnumerable<PromptRequest>> GetAllAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        return await context.Requests
            .AsNoTracking()
            .OrderByDescending(request => request.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(request => Map(request))
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<PromptRequest>> GetByClassificationAsync(string classification, CancellationToken cancellationToken = default)
    {
        return await context.Requests
            .AsNoTracking()
            .Where(request => request.Classification == classification)
            .OrderByDescending(request => request.CreatedAt)
            .Select(request => Map(request))
            .ToListAsync(cancellationToken);
    }

    private static PromptRequest Map(Request entity)
    {
        return new PromptRequest
        {
            Id = entity.Id,
            Prompt = entity.Prompt,
            Classification = entity.Classification,
            Response = entity.Response,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }
}
