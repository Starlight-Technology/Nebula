using MongoDB.Driver;

using Nebula.Mongo.Context;

using MongoConversationMessage = Nebula.Mongo.Context.Entities.ConversationMessage;
using MongoConversationState = Nebula.Mongo.Context.Entities.ConversationState;

namespace Nebula.Agent.Data;

public class MongoConversationMemoryRepository(IMongoContext context) : IConversationMemoryStore
{
    public async Task<ConversationMessage> AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = new MongoConversationMessage
            {
                Id = message.Id == Guid.Empty ? Guid.NewGuid() : message.Id,
                ConversationId = message.ConversationId,
                Role = message.Role,
                Content = message.Content,
                CreatedAt = message.CreatedAt == default ? DateTime.UtcNow : message.CreatedAt
            };

            await context.ConversationMessages.InsertOneAsync(entity, null, cancellationToken);

            return Map(entity);
        }
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed while saving conversation memory.", ex);
        }
        catch (MongoCommandException ex)
        {
            throw new InvalidOperationException("MongoDB command failed while saving conversation memory.", ex);
        }
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        try
        {
            var filter = Builders<MongoConversationMessage>.Filter.Eq(message => message.ConversationId, conversationId);
            var sort = Builders<MongoConversationMessage>.Sort
                .Descending(message => message.CreatedAt)
                .Descending(message => message.Id);

            var entities = await context.ConversationMessages
                .Find(filter)
                .Sort(sort)
                .Limit(limit)
                .ToListAsync(cancellationToken);

            return entities
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(Map)
                .ToList();
        }
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed while reading conversation memory.", ex);
        }
        catch (MongoCommandException ex)
        {
            throw new InvalidOperationException("MongoDB command failed while reading conversation memory.", ex);
        }
    }

    public async Task<ConversationState?> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = await context.ConversationStates
                .Find(state => state.ConversationId == conversationId)
                .FirstOrDefaultAsync(cancellationToken);

            return entity is null ? null : Map(entity);
        }
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed while reading conversation state.", ex);
        }
        catch (MongoCommandException ex)
        {
            throw new InvalidOperationException("MongoDB command failed while reading conversation state.", ex);
        }
    }

    public async Task<ConversationState> UpsertStateAsync(ConversationState state, CancellationToken cancellationToken = default)
    {
        try
        {
            var entity = new MongoConversationState
            {
                ConversationId = state.ConversationId,
                Summary = state.Summary,
                CurrentGoal = state.CurrentGoal,
                CurrentPlan = state.CurrentPlan,
                UpdatedAt = state.UpdatedAt == default ? DateTime.UtcNow : state.UpdatedAt
            };

            var options = new ReplaceOptions { IsUpsert = true };
            await context.ConversationStates.ReplaceOneAsync(
                existingState => existingState.ConversationId == state.ConversationId,
                entity,
                options,
                cancellationToken);

            return Map(entity);
        }
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed while saving conversation state.", ex);
        }
        catch (MongoCommandException ex)
        {
            throw new InvalidOperationException("MongoDB command failed while saving conversation state.", ex);
        }
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetRecentConversationsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        try
        {
            var filter = Builders<MongoConversationState>.Filter.Empty;
            var sort = Builders<MongoConversationState>.Sort.Descending(state => state.UpdatedAt);

            var entities = await context.ConversationStates
                .Find(filter)
                .Sort(sort)
                .Limit(limit)
                .ToListAsync(cancellationToken);

            if (entities.Count == 0)
            {
                return [];
            }

            var summaries = new List<ConversationSummary>(entities.Count);
            foreach (var entity in entities)
            {
                var messageCount = (int)await context.ConversationMessages.CountDocumentsAsync(
                    message => message.ConversationId == entity.ConversationId,
                    cancellationToken: cancellationToken);

                summaries.Add(ConversationSummary.FromState(Map(entity), messageCount));
            }

            return summaries;
        }
        catch (MongoAuthenticationException ex)
        {
            throw new InvalidOperationException("MongoDB authentication failed while listing conversation memory.", ex);
        }
        catch (MongoCommandException ex)
        {
            throw new InvalidOperationException("MongoDB command failed while listing conversation memory.", ex);
        }
    }

    private static ConversationMessage Map(MongoConversationMessage entity)
    {
        return new ConversationMessage
        {
            Id = entity.Id,
            ConversationId = entity.ConversationId,
            Role = entity.Role,
            Content = entity.Content,
            CreatedAt = entity.CreatedAt
        };
    }

    private static ConversationState Map(MongoConversationState entity)
    {
        return new ConversationState
        {
            ConversationId = entity.ConversationId,
            Summary = entity.Summary,
            CurrentGoal = entity.CurrentGoal,
            CurrentPlan = entity.CurrentPlan,
            UpdatedAt = entity.UpdatedAt
        };
    }
}
