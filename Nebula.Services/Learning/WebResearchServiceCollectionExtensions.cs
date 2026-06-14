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
        services.AddSingleton(
            new WebResearchLogSink(log ?? Console.WriteLine));
        services.TryAddSingleton<IFetchedPageCache, InMemoryFetchedPageCache>();
        services.AddSingleton<IDomainRateLimiter, DomainRateLimiter>();
        services.AddSingleton<DirectDocumentationProvider>();
        services.AddSingleton<IContentExtractor, HtmlContentExtractor>();
        services.AddSingleton<DisabledWebResearchService>();
        services.AddHttpClient<BingHtmlSearchProvider>();
        services.AddHttpClient<CachedPageFetcher>();
        services.AddHttpClient<BraveWebResearchService>();
        services.AddTransient<IPageFetcher>(provider =>
            provider.GetRequiredService<CachedPageFetcher>());
        services.AddTransient<FreeSearchProvider>();
        services.AddTransient<ISearchProvider>(provider =>
            options.Provider.Trim().ToLowerInvariant() switch
            {
                "directdocumentation" =>
                    provider.GetRequiredService<DirectDocumentationProvider>(),
                "bing" or "binghtml" =>
                    provider.GetRequiredService<BingHtmlSearchProvider>(),
                _ => provider.GetRequiredService<FreeSearchProvider>()
            });
        services.AddTransient<FreeWebResearchService>();
        services.AddTransient<IWebResearchService>(provider =>
            options.Provider.Trim().ToLowerInvariant() switch
            {
                "brave" =>
                    provider.GetRequiredService<BraveWebResearchService>(),
                "free" or "bing" or "binghtml" or "directdocumentation" =>
                    provider.GetRequiredService<FreeWebResearchService>(),
                "disabled" or "" =>
                    provider.GetRequiredService<DisabledWebResearchService>(),
                "serpapi" =>
                    new UnsupportedWebResearchService(
                        options.Provider,
                        provider.GetRequiredService<WebResearchLogSink>()),
                _ => new UnsupportedWebResearchService(
                    options.Provider,
                    provider.GetRequiredService<WebResearchLogSink>())
            });

        return services;
    }
}
