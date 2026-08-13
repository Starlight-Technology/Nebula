using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nebula.Core.Learning;

namespace Nebula.Services.Learning;

public static class WebResearchServiceCollectionExtensions
{
    public static IServiceCollection AddWebResearch(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = WebResearchOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.Configure<SearXngSearchOptions>(
            configuration.GetSection(SearXngSearchOptions.SectionName));
        services.PostConfigure<SearXngSearchOptions>(
            ApplySearXngEnvironmentOverrides);
        services.AddSingleton(
            new WebResearchLogSink(log ?? Console.WriteLine));
        services.TryAddSingleton<IFetchedPageCache, InMemoryFetchedPageCache>();
        services.AddSingleton<IDomainRateLimiter, DomainRateLimiter>();
        services.AddSingleton<DirectDocumentationProvider>();
        services.AddSingleton<IContentExtractor, HtmlContentExtractor>();
        services.AddSingleton<DisabledWebResearchService>();
        services.AddHttpClient<BingHtmlSearchProvider>();
        services.AddHttpClient<SearXngSearchProvider>();
        services.AddHttpClient<CachedPageFetcher>();
        services.AddHttpClient<BraveWebResearchService>();
        services.AddScoped<IWebSearchOrchestrator>(provider =>
            new WebSearchOrchestrator(
                [
                    provider.GetRequiredService<SearXngSearchProvider>(),
                    provider.GetRequiredService<BingHtmlSearchProvider>()
                ],
                provider.GetService<WebResearchLogSink>()));
        services.AddTransient<IPageFetcher>(provider =>
            provider.GetRequiredService<CachedPageFetcher>());
        services.AddTransient<FreeSearchProvider>();
        services.AddScoped<ISearchProvider, ConfigurableSearchProvider>();
        services.AddScoped<FreeWebResearchService>();
        services.AddScoped<IWebResearchService, ConfigurableWebResearchService>();

        return services;
    }

    private static void ApplySearXngEnvironmentOverrides(
        SearXngSearchOptions options)
    {
        var enabled = Environment.GetEnvironmentVariable(
            "Research__SearXng__Enabled");
        if (bool.TryParse(enabled, out var parsedEnabled))
        {
            options.Enabled = parsedEnabled;
        }

        var baseUrl = Environment.GetEnvironmentVariable(
            "Research__SearXng__BaseUrl");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options.BaseUrl = baseUrl.Trim();
        }

        var maxResults = Environment.GetEnvironmentVariable(
            "Research__SearXng__MaxResults");
        if (int.TryParse(maxResults, out var parsedMaxResults))
        {
            options.MaxResults = parsedMaxResults;
        }

        var timeout = Environment.GetEnvironmentVariable(
            "Research__SearXng__TimeoutSeconds");
        if (int.TryParse(timeout, out var parsedTimeout))
        {
            options.TimeoutSeconds = parsedTimeout;
        }
    }
}
