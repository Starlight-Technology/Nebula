using Corona.Theming;

using Microsoft.EntityFrameworkCore;

using MongoDB.Bson;
using MongoDB.Driver;

using Nebula.Agent;
using Nebula.Agent.Application;
using Nebula.Agent.Infrastructure;
using Nebula.Agent.Data;
using Nebula.App.Shared.State;
using Nebula.App.Web.Components;
using Nebula.App.Shared.Setup;
using Nebula.Core.Agent;
using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;
using Nebula.Core.Learning;
using Nebula.Core.Memory;
using Nebula.Core.MachineLearning;
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

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddSingleton<ILlamaClient>(_ => new LlamaClient());
builder.Services.AddSingleton<ILlamaRuntimeTelemetryService, LlamaRuntimeTelemetryService>();
builder.Services.AddSingleton<IRuntimeSetupAdvisor>(_ => new RuntimeSetupAdvisor("Web app"));
builder.Services.AddSingleton<IOllamaUpdateService, OllamaUpdateService>();
builder.Services.AddSingleton<IProjectDoctorService, ProjectDoctorService>();
builder.Services.AddScoped(_ => new NebulaRuntimeSettings
{
    MainModel =
        builder.Configuration["LLAMA_MODEL"] ??
        builder.Configuration["OLLAMA_MODEL"] ??
        string.Empty,
    LearningModel =
        builder.Configuration["LEARNING_MODEL"] ??
        builder.Configuration["LLAMA_MODEL"] ??
        builder.Configuration["OLLAMA_MODEL"] ??
        string.Empty,
    WebResearchProvider =
        builder.Configuration["WebResearch:Provider"] ??
        NebulaRuntimeSettings.DefaultWebResearchProvider,
    ResponseLanguageCode =
        builder.Configuration["Nebula:ResponseLanguageCode"] ??
        NebulaRuntimeSettings.DefaultLanguageCode,
    ResponseLanguageName =
        builder.Configuration["Nebula:ResponseLanguageName"] ??
        NebulaRuntimeSettings.DefaultLanguageName,
    AutoApproveCommands =
        builder.Configuration.GetValue<bool>("Nebula:AutoApproveCommands"),
    AutoApproveCategories =
        RuntimeSettingsBinding.ParseCategoryList(
            builder.Configuration["Nebula:AutoApproveCategories"]),
    RequireDeterministicVerification =
        builder.Configuration.GetValue(
            "Nebula:RequireDeterministicVerification",
            true),
    CommandTimeoutSeconds =
        builder.Configuration.GetValue(
            "Nebula:CommandTimeoutSeconds",
            300),
    ScriptTimeoutSeconds =
        builder.Configuration.GetValue(
            "Nebula:ScriptTimeoutSeconds",
            300),
    MaxVerificationRetries =
        builder.Configuration.GetValue(
            "Nebula:MaxVerificationRetries",
            2),
    WorkspaceRoot =
        builder.Configuration["Nebula:WorkspaceRoot"] ??
        string.Empty,
    SandboxMode =
        RuntimeSettingsBinding.ParseSandboxMode(
            builder.Configuration["Nebula:Sandbox:Mode"]),
    SandboxImage =
        builder.Configuration["Nebula:Sandbox:Image"] ??
        "mcr.microsoft.com/powershell:lts",
    SandboxMemoryLimitMb =
        builder.Configuration.GetValue(
            "Nebula:Sandbox:MemoryLimitMb",
            0L),
    SandboxCpuLimit =
        builder.Configuration.GetValue(
            "Nebula:Sandbox:CpuLimit",
            0d)
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
builder.Services.AddSingleton<Nebula.Agent.ILogger, ConsoleLogger>();
builder.Services.AddSingleton<DeterministicCommandClassifier>(provider =>
    new DeterministicCommandClassifier(
        Environment.CurrentDirectory,
        provider.GetRequiredService<IScriptContentSafetyClassifier>()));
builder.Services.AddScoped<MlNetCommandClassifier>(provider =>
    new MlNetCommandClassifier(
        provider.GetRequiredService<IMlModelStore>(),
        builder.Configuration["Nebula:CommandSafety:FallbackModelPath"] ??
            builder.Configuration["COMMAND_SAFETY_MODEL"],
        provider.GetRequiredService<Nebula.Agent.ILogger>().Log));
builder.Services.AddScoped<ICommandClassifier>(provider =>
    new CompositeCommandClassifier(
        provider.GetRequiredService<DeterministicCommandClassifier>(),
        provider.GetRequiredService<MlNetCommandClassifier>()));
builder.Services.AddScoped<ICommandPolicyEngine>(provider =>
    new CommandPolicyEngine(
        provider.GetRequiredService<ICommandClassifier>(),
        provider.GetRequiredService<Nebula.Agent.ILogger>().Log));
builder.Services.AddScoped<IOperationPolicyEngine>(provider =>
    new OperationPolicyEngine(
        provider.GetRequiredService<ICommandPolicyEngine>(),
        provider.GetRequiredService<Nebula.Agent.ILogger>().Log));
builder.Services.AddWebResearch(
    builder.Configuration,
    message => Console.WriteLine(message));
builder.Services.AddSingleton<IKnowledgeClassifier>(provider =>
    new KnowledgeClassificationPipeline(
        builder.Configuration["KNOWLEDGE_CLASSIFIER_MODEL"],
        provider.GetRequiredService<Nebula.Agent.ILogger>().Log));
builder.Services.AddSingleton<IKnowledgeScoreEngine, KnowledgeScoreEngine>();
builder.Services.AddSingleton<IKnowledgeAutomationPolicy, KnowledgeAutomationPolicy>();
builder.Services.AddHttpClient<ILearningSourceReader, LearningSourceReader>();
builder.Services.AddScoped<IKnowledgeExtractor>(provider =>
    new LlamaKnowledgeExtractor(
        provider.GetRequiredService<ILlamaClient>(),
        provider.GetRequiredService<IJsonExtractor>(),
        provider.GetRequiredService<NebulaRuntimeSettings>(),
        fallbackExtractor: new KnowledgeExtractor(),
        log: provider.GetRequiredService<Nebula.Agent.ILogger>().Log));
builder.Services.AddScoped<ISafeExperimentRunner, SafeExperimentRunner>();
builder.Services.AddSingleton<IConversationMemoryStore, InMemoryConversationMemoryRepository>();
builder.Services.AddScoped<NebulaContextBuilder>();

var mongoConn = builder.Configuration["MONGO_CONNECTION"] ?? "mongodb://admin:password@localhost:27017/nebula?authSource=admin";
var mongoDb = builder.Configuration["MONGO_DATABASE"] ?? "nebula";

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

var pgConn = builder.Configuration["POSTGRES_CONNECTION"] ?? "Host=localhost;Database=nebula;Username=postgres;Password=postgres123";
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
builder.Services.AddScoped<IWorkspaceMemoryStore, PostgresWorkspaceMemoryStore>();
builder.Services.AddScoped<WorkspaceMemoryService>();
builder.Services.AddScoped<ICommandAllowlistService, CommandAllowlistService>();
builder.Services.AddScoped<IWorkspaceCategoryPolicyService, WorkspaceCategoryPolicyService>();
builder.Services.AddScoped<IPolicySimulator, PolicySimulator>();
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
builder.Services.AddScoped<ICommandApprovalService, CommandApprovalService>();
builder.Services.AddScoped<IConversationContextService, ConversationContextService>();
builder.Services.AddScoped<IAgentActionRunner, AgentActionRunner>();
builder.Services.AddScoped<IManager, Manager>();
builder.Services.AddCoronaTheming(CoronaThemes.Dark());

var app = builder.Build();

await using (var migrationScope = app.Services.CreateAsyncScope())
{
    var postgresContext =
        migrationScope.ServiceProvider.GetRequiredService<PostgresContext>();
    await PostgresDatabaseInitializer.InitializeAsync(postgresContext);
}

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapGet(
    "/api/research/search",
    async (
        string q,
        ISearchProvider searchProvider,
        NebulaRuntimeSettings runtimeSettings,
        WebResearchOptions webResearchOptions,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.BadRequest(new
            {
                error = "Query string parameter 'q' is required."
            });
        }

        var providerName = string.IsNullOrWhiteSpace(
            runtimeSettings.WebResearchProvider)
            ? webResearchOptions.Provider
            : runtimeSettings.WebResearchProvider;
        var results = await searchProvider.SearchAsync(
            q,
            cancellationToken);

        return Results.Ok(new ResearchSearchResponse(
            q,
            results.Select(result => new ResearchSearchResult(
                providerName,
                result.Title,
                result.Url,
                result.Snippet,
                result.SearchScore)).ToList()));
    });

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(Nebula.App.Shared._Imports).Assembly,
        typeof(Nebula.App.Web.Client._Imports).Assembly);

app.Run();

public sealed record ResearchSearchResponse(
    string Query,
    IReadOnlyList<ResearchSearchResult> ProviderResults);

public sealed record ResearchSearchResult(
    string Provider,
    string Title,
    string Url,
    string Snippet,
    double Score);

internal static class RuntimeSettingsBinding
{
    public static SandboxMode ParseSandboxMode(string? value)
    {
        return Enum.TryParse<SandboxMode>(value, ignoreCase: true, out var mode)
            ? mode
            : SandboxMode.Disabled;
    }

    public static List<string> ParseCategoryList(string? value)
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
