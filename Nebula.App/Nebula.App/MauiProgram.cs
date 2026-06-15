using Corona.Theming;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using MongoDB.Bson;
using MongoDB.Driver;

using Nebula.Agent;
using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.App.Shared.Setup;
using Nebula.App.Shared.State;
using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Learning;
using Nebula.Core.MachineLearning;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Mongo.Context;
using Nebula.Postgres.Context;
using Nebula.Runner;
using Nebula.Services.Commands;
using Nebula.Services.Learning;
using Nebula.Services.Operations;
using Nebula.Services.Safety;

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

        builder.Services.AddSingleton<ILlamaClient>(_ => new LlamaClient());
        builder.Services.AddSingleton<ILlamaRuntimeTelemetryService, LlamaRuntimeTelemetryService>();
        builder.Services.AddSingleton<IRuntimeSetupAdvisor>(_ => new RuntimeSetupAdvisor("Native app"));
        builder.Services.AddScoped(_ => new NebulaRuntimeSettings
        {
            MainModel =
                Environment.GetEnvironmentVariable("LLAMA_MODEL") ??
                Environment.GetEnvironmentVariable("OLLAMA_MODEL") ??
                string.Empty,
            LearningModel =
                Environment.GetEnvironmentVariable("LEARNING_MODEL") ??
                Environment.GetEnvironmentVariable("LLAMA_MODEL") ??
                Environment.GetEnvironmentVariable("OLLAMA_MODEL") ??
                string.Empty,
            WebResearchProvider =
                Environment.GetEnvironmentVariable("WebResearch__Provider") ??
                NebulaRuntimeSettings.DefaultWebResearchProvider,
            ResponseLanguageCode =
                Environment.GetEnvironmentVariable("NEBULA_RESPONSE_LANGUAGE_CODE") ??
                NebulaRuntimeSettings.DefaultLanguageCode,
            ResponseLanguageName =
                Environment.GetEnvironmentVariable("NEBULA_RESPONSE_LANGUAGE_NAME") ??
                NebulaRuntimeSettings.DefaultLanguageName
        });
        builder.Services.AddScoped<NebulaWorkspaceState>();
        builder.Services.AddSingleton<IShellExecutor, ShellExecutor>();
        builder.Services.AddSingleton<IRuntimeCommandEnvironmentDetector, RuntimeCommandEnvironmentDetector>();
        builder.Services.AddSingleton<ICommandIntentParser, CommandIntentParser>();
        builder.Services.AddSingleton<ICommandResolver, CommandResolver>();
        builder.Services.AddSingleton<IOperationKindDetector, OperationKindDetector>();
        builder.Services.AddSingleton<IExecutionEvidenceCollector, ExecutionEvidenceCollector>();
        builder.Services.AddSingleton<IFileWriteSafetyClassifier, FileWriteSafetyClassifier>();
        builder.Services.AddSingleton<IScriptContentSafetyClassifier, ScriptContentSafetyClassifier>();
        builder.Services.AddSingleton<IJsonExtractor, JsonExtractor>();
        builder.Services.AddSingleton<Agent.ILogger, ConsoleLogger>();
        builder.Services.AddSingleton<DeterministicCommandClassifier>(provider =>
            new DeterministicCommandClassifier(
                Environment.CurrentDirectory,
                provider.GetRequiredService<IScriptContentSafetyClassifier>()));
        builder.Services.AddScoped<MlNetCommandClassifier>(provider =>
            new MlNetCommandClassifier(
                provider.GetRequiredService<IMlModelStore>(),
                builder.Configuration[
                    "Nebula:CommandSafety:FallbackModelPath"] ??
                    Environment.GetEnvironmentVariable(
                        "COMMAND_SAFETY_MODEL"),
                provider.GetRequiredService<Agent.ILogger>().Log));
        builder.Services.AddScoped<ICommandClassifier>(provider =>
            new CompositeCommandClassifier(
                provider.GetRequiredService<DeterministicCommandClassifier>(),
                provider.GetRequiredService<MlNetCommandClassifier>()));
        builder.Services.AddScoped<ICommandPolicyEngine>(provider =>
            new CommandPolicyEngine(
                provider.GetRequiredService<ICommandClassifier>(),
                provider.GetRequiredService<Agent.ILogger>().Log));
        builder.Services.AddScoped<IOperationPolicyEngine>(provider =>
            new OperationPolicyEngine(
                provider.GetRequiredService<ICommandPolicyEngine>(),
                provider.GetRequiredService<Agent.ILogger>().Log));
        builder.Services.AddWebResearch(
            builder.Configuration,
            message => Console.WriteLine(message));
        builder.Services.AddSingleton<IKnowledgeClassifier>(provider =>
            new KnowledgeClassificationPipeline(
                Environment.GetEnvironmentVariable("KNOWLEDGE_CLASSIFIER_MODEL"),
                provider.GetRequiredService<Agent.ILogger>().Log));
        builder.Services.AddSingleton<IKnowledgeScoreEngine, KnowledgeScoreEngine>();
        builder.Services.AddSingleton<IKnowledgeAutomationPolicy, KnowledgeAutomationPolicy>();
        builder.Services.AddScoped<IKnowledgeExtractor, LlamaKnowledgeExtractor>();
        builder.Services.AddScoped<ISafeExperimentRunner, SafeExperimentRunner>();
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
        builder.Services.AddScoped<IMlModelStore, PostgresMlModelStore>();
        builder.Services.AddScoped<ICommandRepository, PostgresCommandRepository>();
        builder.Services.AddScoped<IPromptRequestStore, PostgresPromptRequestRepository>();
        builder.Services.AddScoped<IPromptRequestRepository, CompositePromptRequestRepository>();
        builder.Services.AddScoped<IConversationMemoryStore, PostgresConversationMemoryRepository>();
        builder.Services.AddScoped<IConversationMemoryRepository, CompositeConversationMemoryRepository>();
        builder.Services.AddScoped<IKnowledgeStore, PostgresKnowledgeStore>();
        builder.Services.AddScoped<IFetchedPageCache, PostgresFetchedPageCache>();
        builder.Services.AddScoped<ILearningEngine, LearningEngine>();
        builder.Services.AddScoped<IKnowledgeQueryService, KnowledgeQueryService>();
        builder.Services.AddScoped<IAgentActionRunner, AgentActionRunner>();
        builder.Services.AddScoped<IManager, Manager>();

        builder.Services.AddCoronaTheming(CoronaThemes.Dark());

        var app = builder.Build();

        using (var migrationScope = app.Services.CreateScope())
        {
            var postgresContext =
                migrationScope.ServiceProvider.GetRequiredService<PostgresContext>();
            PostgresDatabaseInitializer.Initialize(postgresContext);
        }

        return app;
    }
}
