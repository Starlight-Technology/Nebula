using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace Nebula.App.Web.Auth;

public static class WebAuthServiceCollectionExtensions
{
    public static IServiceCollection AddNebulaWebAuth(
        this IServiceCollection services,
        WebAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return services;
        }

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, cookie =>
            {
                cookie.Cookie.Name = options.CookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.LoginPath = "/login";
                cookie.LogoutPath = "/logout";
                cookie.AccessDeniedPath = "/login";
                cookie.ReturnUrlParameter = "returnUrl";
                cookie.ExpireTimeSpan = options.SessionLifetime;
                cookie.SlidingExpiration = true;
                cookie.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context => HandleRedirectAsync(
                        context,
                        StatusCodes.Status401Unauthorized,
                        "unauthorized"),
                    OnRedirectToAccessDenied = context => HandleRedirectAsync(
                        context,
                        StatusCodes.Status403Forbidden,
                        "forbidden")
                };
            });

        services.AddAuthorization(authorization =>
        {
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    private static Task HandleRedirectAsync(
        RedirectContext<CookieAuthenticationOptions> context,
        int statusCode,
        string error)
    {
        if (IsApiRequest(context.Request))
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync($$"""{"error":"{{error}}"}""");
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    private static bool IsApiRequest(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api")
            || request.Path.StartsWithSegments("/_blazor")
            || request.Headers.Accept.Any(value =>
                value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
    }
}
