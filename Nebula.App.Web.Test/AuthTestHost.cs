using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nebula.App.Web.Auth;

namespace Nebula.App.Web.Test;

internal sealed class AuthTestHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly HttpClient client;
    private readonly CookieContainer cookies = new();

    private AuthTestHost(WebApplication app, TestServer server)
    {
        this.app = app;
        client = new HttpClient(server.CreateHandler(), disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost")
        };
    }

    public static async Task<AuthTestHost> StartAsync(WebAuthOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(options);
        builder.Services.AddAntiforgery();
        builder.Services.AddNebulaWebAuth(options);

        var app = builder.Build();

        if (options.Enabled)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.UseAntiforgery();
        app.MapNebulaWebAuthEndpoints();

        app.MapGet("/", () => "home");
        app.MapGet("/fallback", () => "fallback");
        app.MapGet("/api/test", () => Results.Ok(new { ok = true }));
        app.MapGet("/api/fallback", () => "fallback");
        app.MapPost("/_blazor/negotiate", () => "hub");
        app.MapGet("/public", () => "public").AllowAnonymous();

        await app.StartAsync();
        var server = app.Services.GetRequiredService<IServer>() as TestServer
            ?? throw new InvalidOperationException("TestServer was not registered.");
        return new AuthTestHost(app, server);
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = content
        };

        var cookieHeader = cookies.GetCookieHeader(new Uri("http://localhost"));
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.Add("Cookie", cookieHeader);
        }

        var response = await client.SendAsync(request);
        CaptureCookies(response);
        return response;
    }

    public async Task<HttpResponseMessage> LoginAsync(
        string token,
        string returnUrl = "/")
    {
        var (antiforgeryToken, fieldName) = await GetLoginFormAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [fieldName] = antiforgeryToken,
            ["token"] = token,
            ["returnUrl"] = returnUrl
        });

        return await SendAsync(HttpMethod.Post, "/login", form);
    }

    private async Task<(string Token, string FieldName)> GetLoginFormAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "/login");
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(
            html,
            "name=\"(?<name>[^\"]*RequestVerificationToken[^\"]*)\"\\s+value=\"(?<token>[^\"]+)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Assert.True(match.Success, "Antiforgery field not found in login page.");
        return (match.Groups["token"].Value, match.Groups["name"].Value);
    }

    private void CaptureCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            cookies.SetCookies(new Uri("http://localhost"), value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
    }
}
