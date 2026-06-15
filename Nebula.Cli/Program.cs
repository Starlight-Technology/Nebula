using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

using Nebula.Agent;
using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Core.Commands;
using Nebula.Core.Interactions;
using Nebula.Core.Learning;
using Nebula.Core.MachineLearning;
using Nebula.Core.Operations;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Runner;
using Nebula.Mongo.Context;
using Nebula.Postgres.Context;
using Nebula.Services.Safety;
using Nebula.Services.Safety.Training;
using Nebula.Services.Commands;
using Nebula.Services.Learning;
using Nebula.Services.Operations;
using MongoDB.Bson;
using MongoDB.Driver;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

if (args.Contains("--train-command-safety", StringComparer.OrdinalIgnoreCase))
{
    var trainingDataPath = configuration["COMMAND_SAFETY_TRAINING_DATA"]
        ?? Path.Combine(Environment.CurrentDirectory, "Nebula.Services", "Safety", "Training", "command-training-data.csv");
    var pgConnection = configuration["POSTGRES_CONNECTION"]
        ?? "Host=localhost;Database=nebula;Username=postgres;Password=postgres123";
    var options = new DbContextOptionsBuilder<PostgresContext>()
        .UseNpgsql(pgConnection)
        .Options;
    await using var context = new PostgresContext(options);
    await PostgresDatabaseInitializer.InitializeAsync(context);

    var version = int.TryParse(
        configuration["COMMAND_SAFETY_MODEL_VERSION"],
        out var configuredVersion)
        ? configuredVersion
        : checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    var result = await new CommandSafetyTrainer().TrainAndSaveAsync(
        trainingDataPath,
        version,
        new PostgresMlModelStore(context));

    var fallbackModelPath =
        configuration["Nebula:CommandSafety:FallbackModelPath"] ??
        configuration["COMMAND_SAFETY_MODEL"];
    if (!string.IsNullOrWhiteSpace(fallbackModelPath))
    {
        var fullFallbackPath = Path.GetFullPath(fallbackModelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullFallbackPath)!);
        await File.WriteAllBytesAsync(fullFallbackPath, result.ModelData);
        Console.WriteLine(
            $"Optional command safety fallback saved to '{fullFallbackPath}'.");
    }

    Console.WriteLine(
        $"Command safety model version {result.Version} saved to PostgreSQL. " +
        $"Accuracy={result.Metrics.Accuracy:F4}; " +
        $"F1={result.Metrics.F1Score:F4}; active={result.Activated}.");
    return;
}

var services = new ServiceCollection();

// registre suas interfaces aqui
services.AddSingleton<ILlamaClient>(_ => new LlamaClient());
services.AddSingleton<IShellExecutor, ShellExecutor>();
services.AddSingleton<IRuntimeCommandEnvironmentDetector, RuntimeCommandEnvironmentDetector>();
services.AddSingleton<ICommandIntentParser, CommandIntentParser>();
services.AddSingleton<ICommandResolver, CommandResolver>();
services.AddSingleton<IOperationKindDetector, OperationKindDetector>();
services.AddSingleton<IExecutionEvidenceCollector, ExecutionEvidenceCollector>();
services.AddSingleton<IFileWriteSafetyClassifier, FileWriteSafetyClassifier>();
services.AddSingleton<IScriptContentSafetyClassifier, ScriptContentSafetyClassifier>();
services.AddSingleton<IJsonExtractor, JsonExtractor>();
services.AddSingleton<ILogger, ConsoleLogger>();
services.AddSingleton<DeterministicCommandClassifier>(_ =>
    new DeterministicCommandClassifier(Environment.CurrentDirectory));
services.AddScoped<MlNetCommandClassifier>(provider =>
    new MlNetCommandClassifier(
        provider.GetRequiredService<IMlModelStore>(),
        configuration["Nebula:CommandSafety:FallbackModelPath"] ??
            configuration["COMMAND_SAFETY_MODEL"],
        provider.GetRequiredService<ILogger>().Log));
services.AddScoped<ICommandClassifier>(provider =>
    new CompositeCommandClassifier(
        provider.GetRequiredService<DeterministicCommandClassifier>(),
        provider.GetRequiredService<MlNetCommandClassifier>()));
services.AddScoped<ICommandPolicyEngine>(provider =>
    new CommandPolicyEngine(
        provider.GetRequiredService<ICommandClassifier>(),
        provider.GetRequiredService<ILogger>().Log));
services.AddScoped<IOperationPolicyEngine>(provider =>
    new OperationPolicyEngine(
        provider.GetRequiredService<ICommandPolicyEngine>(),
        provider.GetRequiredService<ILogger>().Log));
services.AddWebResearch(
    configuration,
    message => Console.WriteLine(message));
services.AddSingleton<IKnowledgeClassifier>(provider =>
    new KnowledgeClassificationPipeline(
        configuration["KNOWLEDGE_CLASSIFIER_MODEL"],
        provider.GetRequiredService<ILogger>().Log));
services.AddSingleton<IKnowledgeScoreEngine, KnowledgeScoreEngine>();
services.AddSingleton<IKnowledgeAutomationPolicy, KnowledgeAutomationPolicy>();
services.AddScoped<IKnowledgeExtractor, LlamaKnowledgeExtractor>();
services.AddScoped<ISafeExperimentRunner, SafeExperimentRunner>();
services.AddSingleton<IConversationMemoryStore, InMemoryConversationMemoryRepository>();
services.AddScoped<NebulaContextBuilder>();

var mongoConn = configuration["MONGO_CONNECTION"] ?? "mongodb://admin:password@localhost:27017/nebula?authSource=admin";
var mongoDb = configuration["MONGO_DATABASE"] ?? "nebula";
try
{
    var testClient = new MongoClient(mongoConn);
    var testDb = testClient.GetDatabase(mongoDb);
    testDb.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1)).GetAwaiter().GetResult();

    services.AddSingleton<IMongoContext>(_ => new MongoContext(mongoConn, mongoDb));
    services.AddSingleton<IPromptRequestStore, MongoPromptRequestRepository>();
    services.AddSingleton<IConversationMemoryStore, MongoConversationMemoryRepository>();
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
services.AddScoped<IMlModelStore, PostgresMlModelStore>();
services.AddScoped<ICommandRepository, PostgresCommandRepository>();
services.AddScoped<IPromptRequestStore, PostgresPromptRequestRepository>();
services.AddScoped<IPromptRequestRepository, CompositePromptRequestRepository>();
services.AddScoped<IConversationMemoryStore, PostgresConversationMemoryRepository>();
services.AddScoped<IConversationMemoryRepository, CompositeConversationMemoryRepository>();
services.AddScoped<IKnowledgeStore, PostgresKnowledgeStore>();
services.AddScoped<IFetchedPageCache, PostgresFetchedPageCache>();
services.AddScoped<ILearningEngine, LearningEngine>();
services.AddScoped<IKnowledgeQueryService, KnowledgeQueryService>();

services.AddScoped<IAgentActionRunner, AgentActionRunner>();
services.AddScoped<IManager, Manager>();

var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();

var manager = scope.ServiceProvider.GetRequiredService<IManager>();

Console.WriteLine("Starting LLM");
var response = await manager.ManageResponse(
    new UserMessage("Hello", InteractionMode.Chat));

Console.WriteLine(response);
Console.WriteLine("LLM OK");

Console.WriteLine("Starting LLM");
response = await manager.ManageResponse(
    new UserMessage("list all files on current directory", InteractionMode.Agent));

Console.WriteLine(response);

while (true)
{
    var prompt = Console.ReadLine();
    if (string.IsNullOrEmpty(prompt))
    {
        continue;
    }

    response = await manager.ManageResponse(
        new UserMessage(prompt, InteractionMode.Chat));

    Console.WriteLine(response);

}

