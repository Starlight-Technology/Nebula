using System.Security.Cryptography;
using System.Text;

using Nebula.Core.Sandbox;
using Nebula.Runner;
using Nebula.Sandbox;

var builder = WebApplication.CreateBuilder(args);
var options = SandboxOptions.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ISandboxRuntimeLocator, SandboxRuntimeLocator>();
builder.Services.AddSingleton<IResolvedCommandExecutor, ShellExecutor>();
builder.Services.AddSingleton<SandboxExecutor>();

var app = builder.Build();

app.MapGet("/health", async (
    SandboxExecutor executor,
    SandboxOptions sandboxOptions,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(sandboxOptions.Token) ||
        string.IsNullOrWhiteSpace(sandboxOptions.HostTempRoot))
    {
        return Results.Problem(
            "Sidecar token or host temp root is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var probe = await executor.ProbeRuntimeAsync(cancellationToken);
    if (!probe.Reachable)
    {
        return Results.Problem(
            probe.Error ?? "Container runtime is unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new
    {
        status = "ok",
        runtime = probe.Runtime,
        token_configured = true,
        host_temp_configured = true
    });
});

app.MapPost("/run", async (
    HttpContext context,
    SandboxRunRequest request,
    SandboxOptions sandboxOptions,
    SandboxExecutor executor,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(sandboxOptions.Token))
    {
        return Results.Problem(
            "SANDBOX_TOKEN is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var provided = context.Request.Headers[
        SandboxSidecarCommandSandbox.TokenHeader].ToString();
    if (!TokensEqual(sandboxOptions.Token, provided))
    {
        return Results.Unauthorized();
    }

    try
    {
        var response = await executor.RunAsync(request, cancellationToken);
        return Results.Ok(response);
    }
    catch (SandboxRequestException ex)
    {
        return Results.Problem(ex.Message, statusCode: ex.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();

static bool TokensEqual(string expected, string provided)
{
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var providedBytes = Encoding.UTF8.GetBytes(provided);
    return expectedBytes.Length == providedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
}

public partial class Program;
