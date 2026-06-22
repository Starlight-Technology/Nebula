using Microsoft.Extensions.Configuration;

namespace Nebula.Services.Learning;

public sealed class WebResearchOptions
{
    public const string SectionName = "WebResearch";

    public string Provider { get; set; } = "Free";

    public string ApiKey { get; set; } = string.Empty;

    public int MaxResults { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 20;

    public int CacheDays { get; set; } = 7;

    public int RateLimitMilliseconds { get; set; } = 1000;

    public static WebResearchOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new WebResearchOptions
        {
            Provider = Read(
                configuration,
                "Provider",
                "Free"),
            ApiKey = Read(
                configuration,
                "ApiKey",
                string.Empty),
            MaxResults = ReadInt(
                configuration,
                "MaxResults",
                defaultValue: 5,
                minimum: 1,
                maximum: 20),
            TimeoutSeconds = ReadInt(
                configuration,
                "TimeoutSeconds",
                defaultValue: 20,
                minimum: 1,
                maximum: 120),
            CacheDays = ReadInt(
                configuration,
                "CacheDays",
                defaultValue: 7,
                minimum: 1,
                maximum: 90),
            RateLimitMilliseconds = ReadInt(
                configuration,
                "RateLimitMilliseconds",
                defaultValue: 1000,
                minimum: 100,
                maximum: 10000)
        };
    }

    private static string Read(
        IConfiguration configuration,
        string key,
        string defaultValue)
    {
        var configured = configuration[$"{SectionName}:{key}"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var environment = Environment.GetEnvironmentVariable(
            $"{SectionName}__{key}");
        return string.IsNullOrWhiteSpace(environment)
            ? defaultValue
            : environment.Trim();
    }

    private static int ReadInt(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var value = Read(configuration, key, defaultValue.ToString());
        return int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : defaultValue;
    }
}

public sealed class SearXngSearchOptions
{
    public const string SectionName = "Research:SearXng";

    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "http://localhost:8080";

    public int MaxResults { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 20;

    public string Language { get; set; } = "pt-BR";

    public int SafeSearch { get; set; } = 1;

    public string Categories { get; set; } = "general";
}
