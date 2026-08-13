using Corona.Theming;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using MongoDB.Bson;
using MongoDB.Driver;

using Nebula.Agent;
using Nebula.Agent.Application;
using Nebula.Agent.Data;
using Nebula.Agent.Infrastructure;
using Nebula.App.Shared.Setup;
using Nebula.App.Shared.State;
using Nebula.Core.Agent;
using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Core.Learning;
using Nebula.Core.MachineLearning;
using Nebula.Core.Memory;
using Nebula.Core.Operations;
using Nebula.Core.Projects;
using Nebula.Core.Safety;
using Nebula.Llama.Client;
using Nebula.Mongo.Context;
using Nebula.Postgres.Context;
using Nebula.Runner;
using Nebula.Services.Commands;
using Nebula.Services.Learning;
using Nebula.Services.Operations;
using Nebula.Services.Projects;
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
        builder.Services.AddSingleton<IOllamaUpdateService, OllamaUpdateService>();
        builder.Services.AddSingleton<IProjectDoctorService, ProjectDoctorService>();
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
                NebulaRuntimeSettings.DefaultLanguageName,
            AutoApproveCommands =
                bool.TryParse(
                    Environment.GetEnvironmentVariable("NEBULA_AUTO_APPROVE_COMMANDS"),
                    out var autoApproveCommands) &&
                autoApproveCommands,
            AutoApproveCategories = ParseCategoryList(
                Environment.GetEnvironmentVariable("NEBULA_AUTO_APPROVE_CATEGORIES")),
            RequireDeterministicVerification =
                !bool.TryParse(
                    Environment.GetEnvironmentVariable("NEBULA_REQUIRE_DETERMINISTIC_VERIFICATION"),
                    out var requireVerification) ||
                requireVerification,
            CommandTimeoutSeconds =
                int.TryParse(
                    Environment.GetEnvironmentVariable("NEBULA_COMMAND_TIMEOUT_SECONDS"),
                    out var commandTimeout) && commandTimeout > 0
                    ? commandTimeout
                    : 300,
            ScriptTimeoutSeconds =
                int.TryParse(
                    Environment.GetEnvironmentVariable("NEBULA_SCRIPT_TIMEOUT_SECONDS"),
                    out var scriptTimeout) && scriptTimeout > 0
                    ? scriptTimeout
                    : 300,
            MaxVerificationRetries =
                int.TryParse(
                    Environment.GetEnvironmentVariable("NEBULA_MAX_VERIFICATION_RETRIES"),
                    out var maxVerificationRetries) && maxVerificationRetries > 0
                    ? maxVerificationRetries
                    : 2,
            WorkspaceRoot =
                Environment.GetEnvironmentVariable("NEBULA_WORKSPACE_ROOT") ??
                string.Empty,
            SandboxMode =
                Enum.TryParse<SandboxMode>(
                    Environment.GetEnvironmentVariable("NEBULA_SANDBOX_MODE"),
                    ignoreCase: true,
                    out var sandboxMode)
                    ? sandboxMode
                    : SandboxMode.Disabled,
            SandboxImage =
                Environment.GetEnvironmentVariable("NEBULA_SANDBOX_IMAGE") ??
                "mcr.microsoft.com/powershell:lts",
            SandboxMemoryLimitMb =
                long.TryParse(
                    Environment.GetEnvironmentVariable("NEBULA_SANDBOX_MEMORY_LIMIT_MB"),
                    out var sandboxMemoryLimitMb) && sandboxMemoryLimitMb > 0
                    ? sandboxMemoryLimitMb
                    : 0,
            SandboxCpuLimit =
                double.TryParse(
                    Environment.GetEnvironmentVariable("NEBULA_SANDBOX_CPU_LIMIT"),
                    out var sandboxCpuLimit) && sandboxCpuLimit > 0
                    ? sandboxCpuLimit
                    : 0
        });
        builder.Services.AddScoped<NebulaWorkspaceState>();
        builder.Services.AddSingleton<IShellExecutor, ShellExecutor>();
        builder.Services.AddScoped<ICommandSandbox>(provider =>
            new DockerCommandSandbox(
                (IResolvedCommandExecutor)provider.GetRequiredService<IShellExecutor>(),
                provider.GetRequiredService<NebulaRuntimeSettings>()));
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
builder.Services.AddHttpClient<ILearningSourceReader, LearningSourceReader>();
        builder.Services.AddScoped<IKnowledgeExtractor>(provider =>
            new LlamaKnowledgeExtractor(
                provider.GetRequiredService<ILlamaClient>(),
                provider.GetRequiredService<IJsonExtractor>(),
                provider.GetRequiredService<NebulaRuntimeSettings>(),
                fallbackExtractor: new KnowledgeExtractor(),
                log: provider.GetRequiredService<Agent.ILogger>().Log));
        builder.Services.AddScoped<ISafeExperimentRunner, SafeExperimentRunner>();
        builder.Services.AddSingleton<IConversationMemoryStore, InMemoryConversationMemoryRepository>();
        builder.Services.AddScoped<NebulaContextBuilder>();
        builder.Services.AddScoped<IConversationContextService, ConversationContextService>();

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
        builder.Services.AddScoped<IAgentRunStore, PostgresAgentRunStore>();
        builder.Services.AddScoped<IFetchedPageCache, PostgresFetchedPageCache>();
        builder.Services.AddScoped<ILearningEngine, LearningEngine>();
        builder.Services.AddScoped<IKnowledgeQueryService, KnowledgeQueryService>();
        builder.Services.AddScoped<ILearningFromExecutionService, LearningFromExecutionService>();
        builder.Services.AddScoped<IPostTaskLearningService, PostTaskLearningService>();
        builder.Services.AddScoped<IOutputVerificationService, OutputVerificationService>();
        builder.Services.AddScoped<IWorkspaceStackDetector, DeterministicStackDetector>();
        builder.Services.AddScoped<IDeterministicVerificationService, DeterministicVerificationService>();
        builder.Services.AddScoped<ITranslationService, TranslationService>();
        builder.Services.AddScoped<IProjectTemplateCatalog, ProjectTemplateCatalog>();
        builder.Services.AddScoped<IWorkspaceMapService, WorkspaceMapService>();
        builder.Services.AddScoped<IProjectScaffolder, ProjectScaffolder>();
        builder.Services.AddScoped<IProjectStackValidator, ProjectStackValidator>();
        builder.Services.AddScoped<IPlannedPatchApplier, PlannedPatchApplier>();
        builder.Services.AddScoped<IGitDiffService, GitDiffService>();
        builder.Services.AddScoped<IWorkspaceMemoryStore, PostgresWorkspaceMemoryStore>();
        builder.Services.AddScoped<WorkspaceMemoryService>();
        builder.Services.AddScoped<ICommandAllowlistService, CommandAllowlistService>();
        builder.Services.AddScoped<IWorkspaceCategoryPolicyService, WorkspaceCategoryPolicyService>();
        builder.Services.AddScoped<IPolicySimulator, PolicySimulator>();
        builder.Services.AddScoped<ICommandApprovalService, CommandApprovalService>();
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

    private static List<string> ParseCategoryList(string? value)
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
}
