using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

using System;

namespace Nebula.Mongo.Context.Entities;

public class ConversationState
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid ConversationId { get; set; }

    public string? Summary { get; set; }

    public string? CurrentGoal { get; set; }

    public string? CurrentPlan { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
