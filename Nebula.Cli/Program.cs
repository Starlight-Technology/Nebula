using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;

using Nebula.Agent;
using Nebula.Agent.Application;
using Nebula.Agent.Application.Background;
using Nebula.Agent.Application.Events;
using Nebula.Agent.Data;
using Nebula.Agent.Infrastructure;
using Nebula.Agent.Infrastructure.Events;
using Nebula.Agent.Infrastructure.Execution;
using Nebula.Core.Agent;
using Nebula.Core.Background;
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

var hostBuilder = Host.CreateApplicationBuilder(args);
var services = hostBuilder.Services;

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
    UserId = configuration["Nebula:UserId"] ?? string.Empty,
    AutonomyProfile =
        Enum.TryParse<AutonomyProfile>(
            configuration["Nebula:AutonomyProfile"],
            ignoreCase: true,
            out var autonomyProfile)
            ? autonomyProfile
            : AutonomyProfile.Standard,
    ProviderMode =
        Enum.TryParse<ProviderMode>(
            configuration["Nebula:ProviderMode"],
            ignoreCase: true,
            out var providerMode)
            ? providerMode
            : NebulaRuntimeSettings.GetDefaultProviderModeForProfile(
                Enum.TryParse<AutonomyProfile>(
                    configuration["Nebula:AutonomyProfile"],
                    ignoreCase: true,
                    out var profile)
                    ? profile
                    : AutonomyProfile.Standard),
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
services.AddSingleton<ILlamaClient>(provider =>
{
    var settings = provider.GetRequiredService<NebulaRuntimeSettings>();
    return new LlamaClient(timeoutSeconds: settings.LlmTimeoutSeconds);
});
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
services.AddSingleton<IFileWriteSafetyClassifier>(provider =>
    new FileWriteSafetyClassifier(
        ReferenceWorkspace.Resolve(
            provider.GetRequiredService<NebulaRuntimeSettings>().WorkspaceRoot).Root));
services.AddSingleton<IScriptContentSafetyClassifier, ScriptContentSafetyClassifier>();
services.AddSingleton<IJsonExtractor, JsonExtractor>();
services.AddSingleton<ILogger, ConsoleLogger>();
services.AddSingleton<DeterministicCommandClassifier>(provider =>
    new DeterministicCommandClassifier(
        ReferenceWorkspace.Resolve(
            provider.GetRequiredService<NebulaRuntimeSettings>().WorkspaceRoot).Root));
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
services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
services.AddHostedService<BackgroundTaskHostedService>();
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
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    testDb.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token).GetAwaiter().GetResult();

    services.AddSingleton<IMongoContext>(_ => new MongoContext(mongoConn, mongoDb));
    services.AddSingleton<IPromptRequestStore, MongoPromptRequestRepository>();
    services.AddSingleton<IConversationMemoryStore, MongoConversationMemoryRepository>();
}
catch (MongoAuthenticationException ex)
{
    Console.WriteLine("Warning: MongoDB authentication failed. Falling back to NoOpPromptRequestRepository. " + ex.Message);
    services.AddSingleton<IPromptRequestStore, NoOpPromptRequestRepository>();
}
catch (OperationCanceledException)
{
    Console.WriteLine("Warning: MongoDB ping timed out after 2s. Falling back to NoOpPromptRequestRepository.");
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
services.AddScoped<IAgentRunStore, PostgresAgentRunStore>();
services.AddScoped<IFetchedPageCache, PostgresFetchedPageCache>();
services.AddScoped<ILearningEngine, LearningEngine>();
services.AddScoped<IKnowledgeQueryService, KnowledgeQueryService>();
services.AddScoped<ILearningFromExecutionService, LearningFromExecutionService>();
services.AddScoped<IPostTaskLearningService, PostTaskLearningService>();
services.AddScoped<IOutputVerificationService, OutputVerificationService>();
services.AddScoped<ITranslationService, TranslationService>();
services.AddScoped<IErrorFixService, ErrorFixService>();
services.AddScoped<IEnvironmentValidator, EnvironmentValidator>();
services.AddScoped<IResultValidator, ResultValidator>();
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
services.AddScoped<IPolicySimulator>(provider => new PolicySimulator(
    provider.GetRequiredService<ICommandIntentParser>(),
    provider.GetRequiredService<ICommandResolver>(),
    provider.GetRequiredService<IRuntimeCommandEnvironmentDetector>(),
    provider.GetRequiredService<IOperationKindDetector>(),
    provider.GetRequiredService<ICommandPolicyEngine>(),
    provider.GetRequiredService<IOperationPolicyEngine>(),
    provider.GetRequiredService<ICommandApprovalService>(),
    provider.GetRequiredService<IWorkspaceCategoryPolicyService>(),
    provider.GetRequiredService<NebulaRuntimeSettings>()));
services.AddScoped<IGitDiffService, GitDiffService>();
services.AddScoped<Nebula.Core.Agent.IGitService, Nebula.Agent.Infrastructure.GitService>();
services.AddScoped<Nebula.Core.Agent.ITestRunnerService, Nebula.Agent.Infrastructure.TestRunnerService>();
services.AddScoped<Nebula.Core.Agent.ISmokeTestService, Nebula.Agent.Infrastructure.SmokeTestService>();
services.AddScoped<Nebula.Core.Agent.IPortDetectionService, Nebula.Agent.Infrastructure.PortDetectionService>();
services.AddScoped<Nebula.Core.Agent.IPullRequestService, Nebula.Agent.Infrastructure.PullRequestService>();
services.AddScoped<Nebula.Core.Agent.IMigrationService, Nebula.Agent.Infrastructure.MigrationService>();
services.AddScoped<Nebula.Core.Agent.IScreenshotService, Nebula.Agent.Infrastructure.ScreenshotService>();
services.AddHttpClient();
services.AddScoped<ICommandApprovalService, CommandApprovalService>();
services.AddAgentRunProcessing();

services.AddScoped<IAgentActionRunner>(provider => new AgentActionRunner(
    provider.GetRequiredService<ILlamaClient>(),
    provider.GetRequiredService<IShellExecutor>(),
    provider.GetRequiredService<IJsonExtractor>(),
    provider.GetRequiredService<ILogger>(),
    commandRepository: provider.GetRequiredService<ICommandRepository>(),
    maxRetries:
        int.TryParse(configuration["NEBULA_MAX_ACTION_RETRIES"], out var runnerMaxRetries)
            ? runnerMaxRetries
            : 6,
    maxSteps:
        int.TryParse(configuration["NEBULA_MAX_ACTION_STEPS"], out var runnerMaxSteps)
            ? runnerMaxSteps
            : 15,
    commandPolicyEngine: provider.GetRequiredService<ICommandPolicyEngine>(),
    runtimeSettings: provider.GetRequiredService<NebulaRuntimeSettings>(),
    agentRunStore: provider.GetRequiredService<IAgentRunStore>(),
    commandIntentParser: provider.GetRequiredService<ICommandIntentParser>(),
    commandResolver: provider.GetRequiredService<ICommandResolver>(),
    environmentDetector: provider.GetRequiredService<IRuntimeCommandEnvironmentDetector>(),
    operationKindDetector: provider.GetRequiredService<IOperationKindDetector>(),
    scriptContentClassifier: provider.GetRequiredService<IScriptContentSafetyClassifier>(),
    fileWriteSafetyClassifier: provider.GetRequiredService<IFileWriteSafetyClassifier>(),
    operationPolicyEngine: provider.GetRequiredService<IOperationPolicyEngine>(),
    evidenceCollector: provider.GetRequiredService<IExecutionEvidenceCollector>(),
    learningEngine: provider.GetRequiredService<ILearningEngine>(),
    knowledgeQueryService: provider.GetRequiredService<IKnowledgeQueryService>(),
    learningFromExecution: provider.GetRequiredService<ILearningFromExecutionService>(),
    postTaskLearning: provider.GetRequiredService<IPostTaskLearningService>(),
    outputVerificationService: provider.GetRequiredService<IOutputVerificationService>(),
    translationService: provider.GetRequiredService<ITranslationService>(),
    deterministicVerificationService: provider.GetRequiredService<IDeterministicVerificationService>(),
    projectTemplateCatalog: provider.GetRequiredService<IProjectTemplateCatalog>(),
    projectScaffolder: provider.GetRequiredService<IProjectScaffolder>(),
    projectStackValidator: provider.GetRequiredService<IProjectStackValidator>(),
    workspaceMapService: provider.GetRequiredService<IWorkspaceMapService>(),
    plannedPatchApplier: provider.GetRequiredService<IPlannedPatchApplier>(),
    gitDiffService: provider.GetRequiredService<IGitDiffService>(),
    commandSandbox: provider.GetRequiredService<ICommandSandbox>(),
    approvalService: provider.GetRequiredService<ICommandApprovalService>(),
    workspaceMemoryService: provider.GetRequiredService<WorkspaceMemoryService>(),
    commandAllowlistService: provider.GetRequiredService<ICommandAllowlistService>(),
    workspaceCategoryPolicyService: provider.GetRequiredService<IWorkspaceCategoryPolicyService>(),
    knowledgeSearchService: provider.GetRequiredService<IKnowledgeSearchService>(),
    projectDocumentationIndexer: provider.GetRequiredService<IProjectDocumentationIndexer>(),
    userMemoryService: provider.GetRequiredService<IUserMemoryService>()));

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
    commandPolicyEngine: provider.GetRequiredService<ICommandPolicyEngine>(),
    orchestrator: provider.GetRequiredService<IAgentRunOrchestrator>()));

using var host = hostBuilder.Build();
await host.StartAsync();
using var scope = host.Services.CreateScope();

await PostgresDatabaseInitializer.InitializeAsync(
    scope.ServiceProvider.GetRequiredService<PostgresContext>());

var llamaClient = scope.ServiceProvider.GetRequiredService<ILlamaClient>();
await llamaClient.EnsureInstalledModelSelectedAsync();

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
    var progressLock = new object();
    var lastDisplayedPlan = string.Empty;
    var displayedEventCount = 0;
    var progress = new Progress<ConversationTurn>(update =>
    {
        lock (progressLock)
        {
            if (!string.IsNullOrWhiteSpace(update.CurrentPlan) &&
                !string.Equals(lastDisplayedPlan, update.CurrentPlan, StringComparison.Ordinal))
            {
                Console.WriteLine("[PLANO]");
                Console.WriteLine(update.CurrentPlan);
                lastDisplayedPlan = update.CurrentPlan;
            }

            foreach (var actionEvent in update.ActionEvents.Skip(displayedEventCount))
            {
                Console.WriteLine(
                    $"[{actionEvent.Kind}] " +
                    $"{actionEvent.Title}: {TruncateLine(actionEvent.Message)}");
            }

            displayedEventCount = Math.Max(displayedEventCount, update.ActionEvents.Count);
        }
    });
    var turnTask = manager.ManageConversationAsync(
        new UserMessage(prompt, InteractionMode.Agent)
        {
            WorkspaceRoot = runtimeSettings.WorkspaceRoot
        },
        progress,
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
    lock (progressLock)
    {
        PrintPlan(turn, ref lastDisplayedPlan);
    }
    PrintTurn(turn);

    const int maxApprovalRounds = 10;
    var approvalRounds = 0;
    var declinedCommands = new HashSet<Guid>();
    while (turn.ActionStatus == ActionExecutionStatus.AwaitingApproval &&
           approvalRounds < maxApprovalRounds)
    {
        var pending = turn.Commands
            .Where(IsPendingApproval)
            .Where(command => !declinedCommands.Contains(command.StepId))
            .ToList();
        if (pending.Count == 0)
        {
            break;
        }

        var approvedAny = false;
        foreach (var command in pending)
        {
            Console.Write($"Executar aprovado? [s/N] {command.Run}: ");
            var answer = Console.ReadLine();
            if (!IsApproval(answer))
            {
                Console.WriteLine("Aprovacao recusada.");
                declinedCommands.Add(command.StepId);
                continue;
            }

            approvalRounds++;
            turn = await manager.RunApprovedCommandAsync(
                command,
                progress: null,
                cancellationToken: cancellationSource.Token);
            PrintPlan(turn, ref lastDisplayedPlan);
            PrintTurn(turn);
            approvedAny = true;
            break;
        }

        if (!approvedAny)
        {
            break;
        }
    }

    if (turn.ActionStatus == ActionExecutionStatus.AwaitingApproval)
    {
        Console.WriteLine(
            approvalRounds >= maxApprovalRounds
                ? "(limite de aprovacoes por turno atingido)"
                : "(aprovacoes pendentes encerradas)");
    }

    Console.WriteLine("[=== turno concluido ===]");
}

await host.StopAsync();

static void PrintPlan(ConversationTurn turn, ref string lastPlan)
{
    if (!string.IsNullOrWhiteSpace(turn.CurrentPlan) &&
        !string.Equals(lastPlan, turn.CurrentPlan, StringComparison.Ordinal))
    {
        Console.WriteLine("[PLANO]");
        Console.WriteLine(turn.CurrentPlan);
        lastPlan = turn.CurrentPlan;
    }
}

static void PrintTurn(ConversationTurn turn)
{
    Console.WriteLine($"[STATUS] {turn.ActionStatus}");
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
}

static bool IsPendingApproval(CommandExecution command)
{
    return command.SafetyDecision == CommandSafetyDecisionType.AskApproval &&
           !command.Executed &&
           !command.ApprovedByUser &&
           !command.AutoApproved &&
           !string.IsNullOrWhiteSpace(command.Run);
}

static bool IsApproval(string? answer)
{
    var normalized = answer?.Trim();
    return normalized?.Equals("s", StringComparison.OrdinalIgnoreCase) == true ||
           normalized?.Equals("y", StringComparison.OrdinalIgnoreCase) == true;
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

