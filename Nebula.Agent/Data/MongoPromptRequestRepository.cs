using MongoDB.Driver;
using Nebula.Mongo.Context;
using Nebula.Mongo.Context.Entities;

namespace Nebula.Agent.Data;

public class MongoPromptRequestRepository : IPromptRequestStore
{
    private readonly IMongoContext context;

    public MongoPromptRequestRepository(IMongoContext context)
    {
        this.context = context;
    }

    public async Task<PromptRequest> SaveAsync(PromptRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = new Nebula.Mongo.Context.Entities.PromptRequest
            {
                Id = request.Id,
                Prompt = request.Prompt,
                Classification = request.Classification,
                Response = request.Response,
                CreatedAt = request.CreatedAt,
                UpdatedAt = request.UpdatedAt
            };

            await context.PromptRequests.InsertOneAsync(entity, null, cancellationToken);

            request.Id = entity.Id;
            return request;
        }
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed. Ensure your MONGO_CONNECTION includes valid credentials or run a local Mongo instance without auth.", ex);
        }
        catch (MongoCommandException ex)
        {
            // common case when server requires auth
            throw new InvalidOperationException("MongoDB command failed (possible authentication required). Set MONGO_CONNECTION env var with credentials or use the NoOpPromptRequestRepository for local development.", ex);
        }
    }

    public async Task<PromptRequest?> UpdateResponseAsync(Guid id, string response, CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var filter = Builders<Nebula.Mongo.Context.Entities.PromptRequest>.Filter.Eq(request => request.Id, id);
            var update = Builders<Nebula.Mongo.Context.Entities.PromptRequest>.Update
                .Set(request => request.Response, response)
                .Set(request => request.UpdatedAt, now);

            var options = new FindOneAndUpdateOptions<Nebula.Mongo.Context.Entities.PromptRequest>
            {
                ReturnDocument = ReturnDocument.After
            };

            var entity = await context.PromptRequests.FindOneAndUpdateAsync(filter, update, options, cancellationToken);
            if (entity == null)
            {
                return null;
            }

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
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed. Ensure your MONGO_CONNECTION includes valid credentials or run a local Mongo instance without auth.", ex);
        }
        catch (MongoCommandException ex)
        {
            throw new InvalidOperationException("MongoDB command failed (possible authentication required). Set MONGO_CONNECTION env var with credentials or use the NoOpPromptRequestRepository for local development.", ex);
        }
    }

    public async Task<IEnumerable<PromptRequest>> GetAllAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await context.PromptRequests.Find(_ => true).Skip(skip).Limit(take).ToListAsync(cancellationToken);
            return list.Select(x => new PromptRequest
            {
                Id = x.Id,
                Prompt = x.Prompt,
                Classification = x.Classification,
                Response = x.Response,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt
            });
        }
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed. Ensure your MONGO_CONNECTION includes valid credentials or run a local Mongo instance without auth.", ex);
        }
        catch (MongoCommandException ex)
        {
            throw new InvalidOperationException("MongoDB command failed (possible authentication required). Set MONGO_CONNECTION env var with credentials or use the NoOpPromptRequestRepository for local development.", ex);
        }
    }

    public async Task<PromptRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await context.PromptRequests.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken);
            if (entity == null) return null;
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
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed. Ensure your MONGO_CONNECTION includes valid credentials or run a local Mongo instance without auth.", ex);
        }
        catch (MongoCommandException ex)
        {
            throw new InvalidOperationException("MongoDB command failed (possible authentication required). Set MONGO_CONNECTION env var with credentials or use the NoOpPromptRequestRepository for local development.", ex);
        }
    }

    public async Task<IEnumerable<PromptRequest>> GetByClassificationAsync(string classification, CancellationToken cancellationToken = default)
    {
        try
        {
            var list = await context.PromptRequests.Find(x => x.Classification == classification).ToListAsync(cancellationToken);
            return list.Select(x => new PromptRequest
            {
                Id = x.Id,
                Prompt = x.Prompt,
                Classification = x.Classification,
                Response = x.Response,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt
            });
        }
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed. Ensure your MONGO_CONNECTION includes valid credentials or run a local Mongo instance without auth.", ex);
        }
        catch (MongoCommandException ex)
        {
            throw new InvalidOperationException("MongoDB command failed (possible authentication required). Set MONGO_CONNECTION env var with credentials or use the NoOpPromptRequestRepository for local development.", ex);
        }
    }
}
