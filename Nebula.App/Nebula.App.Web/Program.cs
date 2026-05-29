using Corona.Theming;

using Microsoft.EntityFrameworkCore;

using MongoDB.Bson;
using MongoDB.Driver;

using Nebula.Agent;
using Nebula.Agent.Data;
using Nebula.App.Shared.State;
using Nebula.App.Web.Components;
using Nebula.App.Shared.Setup;
using Nebula.Llama.Client;
using Nebula.Mongo.Context;
using Nebula.Postgres.Context;
using Nebula.Runner;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddSingleton<ILlamaClient, LlamaClient>();
builder.Services.AddSingleton<ILlamaRuntimeTelemetryService, LlamaRuntimeTelemetryService>();
builder.Services.AddSingleton<IRuntimeSetupAdvisor>(_ => new RuntimeSetupAdvisor("Web app"));
builder.Services.AddScoped<NebulaWorkspaceState>();
builder.Services.AddSingleton<IShellExecutor, ShellExecutor>();
builder.Services.AddSingleton<IJsonExtractor, JsonExtractor>();
builder.Services.AddSingleton<Nebula.Agent.ILogger, ConsoleLogger>();
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
builder.Services.AddScoped<ICommandRepository, PostgresCommandRepository>();
builder.Services.AddScoped<IPromptRequestStore, PostgresPromptRequestRepository>();
builder.Services.AddScoped<IPromptRequestRepository, CompositePromptRequestRepository>();
builder.Services.AddScoped<IConversationMemoryStore, PostgresConversationMemoryRepository>();
builder.Services.AddScoped<IConversationMemoryRepository, CompositeConversationMemoryRepository>();
builder.Services.AddScoped<IManager, Manager>();
builder.Services.AddCoronaTheming(CoronaThemes.Dark());

var app = builder.Build();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(
        typeof(Nebula.App.Shared._Imports).Assembly,
        typeof(Nebula.App.Web.Client._Imports).Assembly);

app.Run();
