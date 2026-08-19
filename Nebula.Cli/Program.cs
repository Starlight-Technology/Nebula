using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

using Nebula.Agent;
using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Agent.Infrastructure;
using Nebula.Core.Agent;
using Nebula.Core.Commands;
using Nebula.Core.Interactions;
using Nebula.Core.Learning;
using Nebula.Core.MachineLearning;
using Nebula.Core.Memory;
using Nebula.Core.Operations;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Core.Projects;
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
using Nebula.Services.Projects;
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

services.AddSingleton(_ => new NebulaRuntimeSettings
{
    MainModel = configuration["LLAMA_MODEL"] ?? configuration["OLLAMA_MODEL"] ?? string.Empty,
    LearningModel = configuration["LEARNING_MODEL"] ?? configuration["LLAMA_MODEL"] ?? configuration["OLLAMA_MODEL"] ?? string.Empty,
    WebResearchProvider = configuration["WebResearch:Provider"] ?? NebulaRuntimeSettings.DefaultWebResearchProvider,
    ResponseLanguageCode = configuration["Nebula:ResponseLanguageCode"] ?? NebulaRuntimeSettings.DefaultLanguageCode,
    ResponseLanguageName = configuration["Nebula:ResponseLanguageName"] ?? NebulaRuntimeSettings.DefaultLanguageName,
    AutoApproveCommands =
        bool.TryParse(
            configuration["Nebula:AutoApproveCommands"],
            out var autoApproveCommands)
            ? autoApproveCommands
            : false,
    AutoApproveCategories = ParseCategoryList(
        configuration["Nebula:AutoApproveCategories"]),
    WorkspaceRoot = configuration["Nebula:WorkspaceRoot"] ?? string.Empty,
    SandboxMode =
        Enum.TryParse<SandboxMode>(
            configuration["Nebula:Sandbox:Mode"],
            ignoreCase: true,
            out var sandboxMode)
            ? sandboxMode
            : SandboxMode.Disabled,
    SandboxImage = configuration["Nebula:Sandbox:Image"] ?? "mcr.microsoft.com/powershell:lts",
    SandboxMemoryLimitMb =
        long.TryParse(configuration["Nebula:Sandbox:MemoryLimitMb"], out var sandboxMemoryLimitMb)
            ? sandboxMemoryLimitMb
            : 0,
    SandboxCpuLimit =
        double.TryParse(
            configuration["Nebula:Sandbox:CpuLimit"],
            System.Globalization.CultureInfo.InvariantCulture,
            out var sandboxCpuLimit)
            ? sandboxCpuLimit
            : 0
});
services.AddSingleton<ILlamaClient>(_ => new LlamaClient());
services.AddSingleton<IShellExecutor, ShellExecutor>();
services.AddScoped<ICommandSandbox>(provider =>
    new DockerCommandSandbox(
        (IResolvedCommandExecutor)provider.GetRequiredService<IShellExecutor>(),
        provider.GetRequiredService<NebulaRuntimeSettings>()));
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
services.AddScoped<IKnowledgeExtractor>(provider =>
    new LlamaKnowledgeExtractor(
        provider.GetRequiredService<ILlamaClient>(),
        provider.GetRequiredService<IJsonExtractor>(),
        provider.GetRequiredService<NebulaRuntimeSettings>(),
        fallbackExtractor: new KnowledgeExtractor(),
        log: provider.GetRequiredService<ILogger>().Log));
services.AddScoped<ISafeExperimentRunner, SafeExperimentRunner>();
services.AddSingleton<IConversationMemoryStore, InMemoryConversationMemoryRepository>();
services.AddScoped<NebulaContextBuilder>();
services.AddScoped<IConversationContextService, ConversationContextService>();

var mongoConn = configuration["MONGO_CONNECTION"] ?? "mongodb://admin:password@localhost:27017/nebula?authSource=admin";
var mongoDb = configuration["MONGO_DATABASE"] ?? "nebula";
try
{
    var mongoSettings = MongoClientSettings.FromConnectionString(mongoConn);
    mongoSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
    mongoSettings.ConnectTimeout = TimeSpan.FromSeconds(3);
    var testClient = new MongoClient(mongoSettings);
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
services.AddScoped<IKnowledgeRepository, PostgresKnowledgeStore>();
services.AddScoped<IFetchedPageCache, PostgresFetchedPageCache>();
services.AddScoped<ILearningEngine, LearningEngine>();
services.AddScoped<IKnowledgeQueryService, KnowledgeQueryService>();
services.AddScoped<IPostTaskLearningService, PostTaskLearningService>();
services.AddScoped<IWorkspaceStackDetector, DeterministicStackDetector>();
services.AddScoped<IDeterministicVerificationService, DeterministicVerificationService>();
services.AddScoped<IProjectTemplateCatalog, ProjectTemplateCatalog>();
services.AddScoped<IWorkspaceMapService, WorkspaceMapService>();
services.AddScoped<IProjectScaffolder, ProjectScaffolder>();
services.AddScoped<IProjectStackValidator, ProjectStackValidator>();
services.AddScoped<IPlannedPatchApplier, PlannedPatchApplier>();
services.AddScoped<IWorkspaceMemoryStore, PostgresWorkspaceMemoryStore>();
services.AddScoped<WorkspaceMemoryService>();
services.AddScoped<IUserMemoryStore, PostgresUserMemoryStore>();
services.AddScoped<IUserMemoryService, UserMemoryService>();
services.AddScoped<IKnowledgeSearchService, KnowledgeSearchService>();
services.AddScoped<IProjectDocumentationIndexer, ProjectDocumentationIndexer>();
services.AddScoped<ICommandAllowlistService, CommandAllowlistService>();
services.AddScoped<IWorkspaceCategoryPolicyService, WorkspaceCategoryPolicyService>();
services.AddScoped<IPolicySimulator, PolicySimulator>();
services.AddScoped<IGitDiffService, GitDiffService>();
services.AddScoped<ICommandApprovalService, CommandApprovalService>();

services.AddScoped<IAgentActionRunner, AgentActionRunner>();
services.AddScoped<IManager>(provider => new Manager(
    provider.GetRequiredService<ILlamaClient>(),
    provider.GetRequiredService<IShellExecutor>(),
    provider.GetRequiredService<IJsonExtractor>(),
    provider.GetRequiredService<ILogger>(),
    commandRepository: provider.GetRequiredService<ICommandRepository>(),
    promptRepository: provider.GetRequiredService<IPromptRequestRepository>(),
    conversationMemoryRepository: provider.GetRequiredService<IConversationMemoryRepository>(),
    contextBuilder: provider.GetRequiredService<NebulaContextBuilder>(),
    maxActionRetries:
        int.TryParse(configuration["NEBULA_MAX_ACTION_RETRIES"], out var maxActionRetries)
            ? maxActionRetries
            : 6,
    maxActionSteps:
        int.TryParse(configuration["NEBULA_MAX_ACTION_STEPS"], out var maxActionSteps)
            ? maxActionSteps
            : 15,
    actionRunner: provider.GetRequiredService<IAgentActionRunner>(),
    conversationContextService: provider.GetRequiredService<IConversationContextService>(),
    commandPolicyEngine: provider.GetRequiredService<ICommandPolicyEngine>()));

var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();

await PostgresDatabaseInitializer.InitializeAsync(
    scope.ServiceProvider.GetRequiredService<PostgresContext>());

var manager = scope.ServiceProvider.GetRequiredService<IManager>();
var runtimeSettings = scope.ServiceProvider.GetRequiredService<NebulaRuntimeSettings>();

Console.WriteLine("Starting LLM");
var response = await manager.ManageResponse(
    new UserMessage("Hello", InteractionMode.Chat)
    {
        WorkspaceRoot = runtimeSettings.WorkspaceRoot
    });

Console.WriteLine(response);
Console.WriteLine("LLM OK");

while (true)
{
    var prompt = Console.ReadLine();
    if (prompt is null)
    {
        break;
    }

    if (string.IsNullOrEmpty(prompt))
    {
        continue;
    }

    if (prompt.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        prompt.Equals("sair", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Bye.");
        break;
    }

    using var cancellationSource = new CancellationTokenSource();
    var turnTask = manager.ManageConversationAsync(
        new UserMessage(prompt, InteractionMode.Agent)
        {
            WorkspaceRoot = runtimeSettings.WorkspaceRoot
        },
        progress: null,
        cancellationToken: cancellationSource.Token);

    while (!turnTask.IsCompleted)
    {
        if (!Console.IsInputRedirected && Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                cancellationSource.Cancel();
                Console.WriteLine("(cancelando...)");
                break;
            }
        }

        await Task.Delay(50);
    }

    var turn = await turnTask;
    Console.WriteLine(turn.Response);

    foreach (var command in turn.Commands)
    {
        Console.WriteLine(
            $"[COMANDO] kind={command.OperationKind} executado={command.Executed} " +
            $"decisao={command.SafetyDecision} sandbox={command.Sandboxed} " +
            $"saida={command.ExitCode} run={command.Run}");
        if (!string.IsNullOrWhiteSpace(command.StandardOutput))
        {
            Console.WriteLine($"    stdout: {TruncateLine(command.StandardOutput)}");
        }

        if (!string.IsNullOrWhiteSpace(command.StandardError))
        {
            Console.WriteLine($"    stderr: {TruncateLine(command.StandardError)}");
        }
    }

    if (turn.ActionStatus == ActionExecutionStatus.AwaitingApproval)
    {
        foreach (var command in turn.Commands.Where(
            command =>
                command.SafetyDecision == CommandSafetyDecisionType.AskApproval &&
                !command.Executed &&
                !command.ApprovedByUser &&
                !command.AutoApproved &&
                !string.IsNullOrWhiteSpace(command.Run)))
        {
            Console.Write($"Executar aprovado? [s/N] {command.Run}: ");
            var answer = Console.ReadLine();
            if (answer?.Trim().Equals("s", StringComparison.OrdinalIgnoreCase) == true ||
                answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
            {
                var approvedTurn = await manager.RunApprovedCommandAsync(
                    command,
                    progress: null,
                    cancellationToken: cancellationSource.Token);
                Console.WriteLine(approvedTurn.Response);
            }
            else
            {
                Console.WriteLine("Aprovacao recusada.");
            }
        }
    }

    Console.WriteLine("[=== turno concluido ===]");
}

static string TruncateLine(string value)
{
    var singleLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
    return singleLine.Length <= 400 ? singleLine : singleLine[..400] + "...";
}

static List<string> ParseCategoryList(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return [];
    }

    return value
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(category => category.Trim().ToLowerInvariant())
        .Where(category => category.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}

