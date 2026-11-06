using System.Security.Claims;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Nebula.App.Web.Auth;

public static class WebAuthEndpointExtensions
{
    public static IEndpointRouteBuilder MapNebulaWebAuthEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<WebAuthOptions>();
        if (!options.Enabled)
        {
            return endpoints;
        }

        endpoints.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (context.User.Identity?.IsAuthenticated == true)
            {
                return Results.LocalRedirect("/");
            }

            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content(
                LoginPage.Render(tokens, context.Request.Query["returnUrl"].ToString()),
                "text/html");
        }).AllowAnonymous();

        endpoints.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                var refreshedTokens = antiforgery.GetAndStoreTokens(context);
                return Results.Content(
                    LoginPage.Render(
                        refreshedTokens,
                        context.Request.Query["returnUrl"].ToString(),
                        "Sessao expirada. Tente novamente."),
                    "text/html",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());
            var providedToken = form["token"].ToString();

            if (!LocalTokenValidator.IsValid(options.Token, providedToken))
            {
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Nebula.Web.Auth");
                logger.LogWarning("Tentativa de login local recusada.");

                var refreshedTokens = antiforgery.GetAndStoreTokens(context);
                return Results.Content(
                    LoginPage.Render(refreshedTokens, returnUrl, "Token invalido."),
                    "text/html",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "local-operator"),
                new Claim(ClaimTypes.Name, "Operador local")
            };
            var identity = new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme);

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.Add(options.SessionLifetime)
                });

            return Results.LocalRedirect(returnUrl);
        })
        .AllowAnonymous()
        .DisableAntiforgery();

        endpoints.MapGet("/logout", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireAuthorization();

        return endpoints;
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        if (!returnUrl.StartsWith('/')
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            || returnUrl.Any(char.IsControl))
        {
            return "/";
        }

        return returnUrl;
    }
}
