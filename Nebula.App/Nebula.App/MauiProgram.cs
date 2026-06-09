using Corona.Theming;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using MongoDB.Bson;
using MongoDB.Driver;

using Nebula.Agent;
using Nebula.Agent.Data;
using Nebula.App.Shared.Setup;
using Nebula.App.Shared.State;
using Nebula.Llama.Client;
using Nebula.Mongo.Context;
using Nebula.Postgres.Context;
using Nebula.Runner;

namespace Nebula.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<ILlamaClient, LlamaClient>();
        builder.Services.AddSingleton<ILlamaRuntimeTelemetryService, LlamaRuntimeTelemetryService>();
        builder.Services.AddSingleton<IRuntimeSetupAdvisor>(_ => new RuntimeSetupAdvisor("Native app"));
        builder.Services.AddScoped<NebulaWorkspaceState>();
        builder.Services.AddSingleton<IShellExecutor, ShellExecutor>();
        builder.Services.AddSingleton<IJsonExtractor, JsonExtractor>();
        builder.Services.AddSingleton<Agent.ILogger, ConsoleLogger>();
        builder.Services.AddSingleton<IConversationMemoryStore, InMemoryConversationMemoryRepository>();
        builder.Services.AddScoped<NebulaContextBuilder>();

        var mongoConn = Environment.GetEnvironmentVariable("MONGO_CONNECTION")
            ?? "mongodb://admin:password@localhost:27017/nebula?authSource=admin";
        var mongoDb = Environment.GetEnvironmentVariable("MONGO_DATABASE") ?? "nebula";

        try
        {
            var testClient = new MongoClient(mongoConn);
            var testDb = testClient.GetDatabase(mongoDb);
            testDb.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)).GetAwaiter().GetResult();

            builder.Services.AddSingleton<IMongoContext>(_ => new MongoContext(mongoConn, mongoDb));
            builder.Services.AddSingleton<IPromptRequestStore, MongoPromptRequestRepository>();
            builder.Services.AddSingleton<IConversationMemoryStore, MongoConversationMemoryRepository>();
        }
        catch (MongoAuthenticationException ex)
        {
            Console.WriteLine("Warning: MongoDB authentication failed. Falling back to NoOpPromptRequestRepository. " + ex.Message);
            builder.Services.AddSingleton<IPromptRequestStore, NoOpPromptRequestRepository>();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Warning: Could not connect to MongoDB. Falling back to NoOpPromptRequestRepository. " + ex.Message);
            builder.Services.AddSingleton<IPromptRequestStore, NoOpPromptRequestRepository>();
        }

        var pgConn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
            ?? "Host=localhost;Database=nebula;Username=postgres;Password=postgres123";
        builder.Services.AddDbContext<PostgresContext>(options => options.UseNpgsql(pgConn));
        builder.Services.AddScoped<ICommandRepository, PostgresCommandRepository>();
        builder.Services.AddScoped<IPromptRequestStore, PostgresPromptRequestRepository>();
        builder.Services.AddScoped<IPromptRequestRepository, CompositePromptRequestRepository>();
        builder.Services.AddScoped<IConversationMemoryStore, PostgresConversationMemoryRepository>();
        builder.Services.AddScoped<IConversationMemoryRepository, CompositeConversationMemoryRepository>();
        builder.Services.AddScoped<IAgentActionRunner, AgentActionRunner>();
        builder.Services.AddScoped<IManager, Manager>();

        builder.Services.AddCoronaTheming(CoronaThemes.Dark());

        return builder.Build();
    }
}
