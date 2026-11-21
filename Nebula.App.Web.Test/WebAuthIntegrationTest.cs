using System.Net;

using Nebula.App.Web.Auth;

namespace Nebula.App.Web.Test;

public sealed class WebAuthIntegrationTest
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task UnauthenticatedApiReturns401()
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());

        using var response = await host.SendAsync(HttpMethod.Get, "/api/test");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            "unauthorized",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnauthenticatedUiRedirectsToLogin()
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());

        using var response = await host.SendAsync(HttpMethod.Get, "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("/login", location);
        Assert.Contains("returnUrl", location);
    }

    [Fact]
    public async Task UnauthenticatedBlazorHubReturns401()
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());

        using var response = await host.SendAsync(
            HttpMethod.Post,
            "/_blazor/negotiate?negotiateVersion=1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PublicEndpointIsAnonymous()
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());

        using var response = await host.SendAsync(HttpMethod.Get, "/public");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LoginWithoutAntiforgeryReturns400()
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = Token,
            ["returnUrl"] = "/"
        });

        using var response = await host.SendAsync(HttpMethod.Post, "/login", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WrongTokenReturns401WithoutAuthCookie()
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());

        using var response = await host.LoginAsync("wrong-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values
            : Array.Empty<string>();
        Assert.DoesNotContain(setCookies, value => value.StartsWith(
            WebAuthOptions.DefaultCookieName + "=",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidTokenSetsHttpOnlyCookieAndAllowsProtectedEndpoints()
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());

        using var login = await host.LoginAsync(Token);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var setCookies = login.Headers.TryGetValues("Set-Cookie", out var values)
            ? values
            : Array.Empty<string>();
        var authCookie = setCookies.Single(value => value.StartsWith(
            WebAuthOptions.DefaultCookieName + "=",
            StringComparison.Ordinal));
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookie, StringComparison.OrdinalIgnoreCase);

        using var home = await host.SendAsync(HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);

        using var api = await host.SendAsync(HttpMethod.Get, "/api/test");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    public async Task ExternalReturnUrlRedirectsToRoot(string returnUrl)
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());

        using var login = await host.LoginAsync(Token, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.ToString());
    }

    [Fact]
    public async Task LogoutClearsSession()
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());

        using var login = await host.LoginAsync(Token);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        using var logout = await host.SendAsync(HttpMethod.Get, "/logout");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.Location?.ToString());

        using var home = await host.SendAsync(HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
    }

    [Fact]
    public async Task FallbackPolicyProtectsUnmarkedApiEndpoint()
    {
        await using var host = await AuthTestHost.StartAsync(EnabledOptions());

        using var response = await host.SendAsync(HttpMethod.Get, "/api/fallback");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DisabledAuthLeavesAppPublic()
    {
        await using var host = await AuthTestHost.StartAsync(new WebAuthOptions
        {
            Enabled = false
        });

        using var home = await host.SendAsync(HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);

        using var login = await host.SendAsync(HttpMethod.Get, "/login");
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
    }

    private static WebAuthOptions EnabledOptions()
    {
        return new WebAuthOptions
        {
            Enabled = true,
            Token = Token
        };
    }
}
