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
        services.AddScoped<ISearchProvider, ConfigurableSearchProvider>();
        services.AddScoped<FreeWebResearchService>();
        services.AddScoped<IWebResearchService, ConfigurableWebResearchService>();

        return services;
    }
}
