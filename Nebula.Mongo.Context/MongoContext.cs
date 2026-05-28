using MongoDB.Driver;
using Nebula.Mongo.Context.Entities;

namespace Nebula.Mongo.Context;

public interface IMongoContext
{
    IMongoCollection<PromptRequest> PromptRequests { get; }
    IMongoCollection<ConversationMessage> ConversationMessages { get; }
    IMongoCollection<ConversationState> ConversationStates { get; }
}

public class MongoContext : IMongoContext
{
    private readonly IMongoDatabase database;

    public MongoContext(string connectionString, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("MongoDB connection string cannot be null or empty.", nameof(connectionString));

        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("MongoDB database name cannot be null or empty.", nameof(databaseName));

        var client = new MongoClient(connectionString);
        database = client.GetDatabase(databaseName);
    }

    public IMongoCollection<PromptRequest> PromptRequests => database.GetCollection<PromptRequest>("prompt_requests");
    public IMongoCollection<ConversationMessage> ConversationMessages => database.GetCollection<ConversationMessage>("conversation_messages");
    public IMongoCollection<ConversationState> ConversationStates => database.GetCollection<ConversationState>("conversation_states");
}
