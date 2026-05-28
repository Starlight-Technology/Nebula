using Microsoft.EntityFrameworkCore;

using Nebula.Postgres.Context;

using PostgresConversationMessage = Nebula.Postgres.Context.Entities.ConversationMessage;
using PostgresConversationState = Nebula.Postgres.Context.Entities.ConversationState;

namespace Nebula.Agent.Data;

public class PostgresConversationMemoryRepository(PostgresContext context) : IConversationMemoryStore
{
    public async Task<ConversationMessage> AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        var entity = new PostgresConversationMessage
        {
            Id = message.Id == Guid.Empty ? Guid.NewGuid() : message.Id,
            ConversationId = message.ConversationId,
            Role = message.Role,
            Content = message.Content,
            CreatedAt = message.CreatedAt == default ? DateTime.UtcNow : message.CreatedAt
        };

        context.ConversationMessages.Add(entity);
        await context.SaveChangesAsync(cancellationToken);

        return Map(entity);
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

        var entities = await context.ConversationMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return entities
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Select(Map)
            .ToList();
    }

    public async Task<ConversationState?> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var entity = await context.ConversationStates
            .AsNoTracking()
            .FirstOrDefaultAsync(state => state.ConversationId == conversationId, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<ConversationState> UpsertStateAsync(ConversationState state, CancellationToken cancellationToken = default)
    {
        var entity = await context.ConversationStates
            .FirstOrDefaultAsync(existingState => existingState.ConversationId == state.ConversationId, cancellationToken);

        if (entity is null)
        {
            entity = new PostgresConversationState
            {
                ConversationId = state.ConversationId
            };
            context.ConversationStates.Add(entity);
        }

        entity.Summary = state.Summary;
        entity.CurrentGoal = state.CurrentGoal;
        entity.CurrentPlan = state.CurrentPlan;
        entity.UpdatedAt = state.UpdatedAt == default ? DateTime.UtcNow : state.UpdatedAt;

        await context.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    private static ConversationMessage Map(PostgresConversationMessage entity)
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

    private static ConversationState Map(PostgresConversationState entity)
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
