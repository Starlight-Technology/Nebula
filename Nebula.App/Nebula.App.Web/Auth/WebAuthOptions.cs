using Microsoft.Extensions.Hosting;

namespace Nebula.App.Web.Auth;

public sealed class WebAuthOptions
{
    public const string ConfigurationKey = "Nebula:Web:AuthToken";
    public const string EnvironmentVariable = "NEBULA_WEB_AUTH_TOKEN";
    public const int MinimumTokenLength = 32;
    public const string DefaultCookieName = "nebula.auth";

    public bool Enabled { get; init; }

    public string Token { get; init; } = string.Empty;

    public string CookieName { get; init; } = DefaultCookieName;

    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromDays(7);

    public static WebAuthOptions FromConfiguration(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var token = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(token))
        {
            token = configuration[EnvironmentVariable];
        }

        token = token?.Trim() ?? string.Empty;
        if (token.Length == 0)
        {
            if (environment.IsDevelopment())
            {
                return new WebAuthOptions { Enabled = false };
            }

            throw new InvalidOperationException(
                $"Web authentication requires a token in {environment.EnvironmentName}. " +
                $"Set {EnvironmentVariable} (or {ConfigurationKey}) to a random value with at least " +
                $"{MinimumTokenLength} characters. Generate one with: openssl rand -hex 32");
        }

        if (token.Length < MinimumTokenLength)
        {
            throw new InvalidOperationException(
                $"Web authentication token must have at least {MinimumTokenLength} characters. " +
                "Generate one with: openssl rand -hex 32");
        }

        return new WebAuthOptions
        {
            Enabled = true,
            Token = token
        };
    }
}
