using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

using System;

namespace Nebula.Mongo.Context.Entities;

public class PromptRequest
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Prompt { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;

    public string? Response { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
