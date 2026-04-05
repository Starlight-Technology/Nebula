using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

using Nebula.Agent;
using Nebula.Agent.Data;
using Nebula.Llama.Client;
using Nebula.Runner;
using Nebula.Mongo.Context;
using Nebula.Postgres.Context;
using MongoDB.Bson;
using MongoDB.Driver;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();

// registre suas interfaces aqui
services.AddSingleton<ILlamaClient, LlamaClient>();
services.AddSingleton<IShellExecutor, ShellExecutor>();
services.AddSingleton<IJsonExtractor, JsonExtractor>();
services.AddSingleton<ILogger, ConsoleLogger>();

var mongoConn = configuration["MONGO_CONNECTION"] ?? "mongodb://admin:password@localhost:27017/nebula?authSource=admin";
var mongoDb = configuration["MONGO_DATABASE"] ?? "nebula";
try
{
    var testClient = new MongoClient(mongoConn);
    var testDb = testClient.GetDatabase(mongoDb);
    testDb.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)).GetAwaiter().GetResult();

    services.AddSingleton<IMongoContext>(_ => new MongoContext(mongoConn, mongoDb));
    services.AddSingleton<IPromptRequestStore, MongoPromptRequestRepository>();
}
catch (MongoAuthenticationException ex)
{
    Console.WriteLine("Warning: MongoDB authentication failed. Falling back to NoOpPromptRequestRepository. " + ex.Message);
    services.AddSingleton<IPromptRequestStore, NoOpPromptRequestRepository>();
}
catch (Exception ex)
{
    Console.WriteLine("Warning: Could not connect to MongoDB. Falling back to NoOpPromptRequestRepository. " + ex.Message);
    services.AddSingleton<IPromptRequestStore, NoOpPromptRequestRepository>();
}

var pgConn = configuration["POSTGRES_CONNECTION"] ?? "Host=localhost;Database=nebula;Username=postgres;Password=postgres123";
services.AddDbContext<PostgresContext>(opts => opts.UseNpgsql(pgConn));
services.AddScoped<ICommandRepository, PostgresCommandRepository>();
services.AddScoped<IPromptRequestStore, PostgresPromptRequestRepository>();
services.AddScoped<IPromptRequestRepository, CompositePromptRequestRepository>();

services.AddScoped<IManager, Manager>();

var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();

var manager = scope.ServiceProvider.GetRequiredService<IManager>();

Console.WriteLine("Starting LLM");
var response = await manager.ManageResponse("Hello");

Console.WriteLine(response);
Console.WriteLine("LLM OK");

Console.WriteLine("Starting LLM");
response = await manager.ManageResponse("list files on c:");

Console.WriteLine(response);

while (true)
{
    var prompt = Console.ReadLine();
    if (string.IsNullOrEmpty(prompt))
    {
        continue;
    }

    response = await manager.ManageResponse(prompt);

    Console.WriteLine(response);

}

