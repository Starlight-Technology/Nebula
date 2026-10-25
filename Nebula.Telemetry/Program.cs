using Nebula.Llama.Client;
using Nebula.Llama.Client.Telemetry;
using Nebula.Telemetry;

var builder = WebApplication.CreateBuilder(args);
var options = TelemetryOptions.FromEnvironment();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IHostHardwareProbe>(_ => new HostHardwareProbe(
    procRoot: options.ProcRoot,
    sysRoot: options.SysRoot,
    hostRoot: options.HostRoot,
    commandRunner: new ProcessRuntimeCommandRunner()));
builder.Services.AddSingleton(_ => new DockerContainerProbe(
    options.DockerSocket,
    options.OllamaContainerName));
builder.Services.AddSingleton<HostTelemetryCollector>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<HostTelemetryCollector>());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/snapshot", (HostTelemetryCollector collector) => Results.Ok(collector.Current));

app.Run();
