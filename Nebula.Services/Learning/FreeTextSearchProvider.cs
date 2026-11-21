using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using Nebula.Core.Learning;

namespace Nebula.Services.Learning;

public sealed class FreeTextSearchProvider(
    IOptions<FreeTextSearchOptions> optionsAccessor) : ISearchProvider
{
    public const string ProviderName = "FreeText";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var options = optionsAccessor.Value;
        if (options.Terms.Count == 0)
        {
            return [];
        }

        var normalizedQuery = query.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var results = new List<SearchResult>();

        foreach (var term in options.Terms)
        {
            if (normalizedQuery.Contains(term.Normalized.ToLowerInvariant()))
            {
                var score = term.Weight;
                results.Add(new SearchResult(
                    $"{term.Name} match",
                    $"nebula://free-text/{term.Slug}",
                    $"Found keyword: {term.Name}",
                    score));
            }
        }

        // Sort by score descending
        results.Sort((a, b) => b.SearchScore.CompareTo(a.SearchScore));

        // Return top results (max 10)
        return results.Take(10).ToList() as IReadOnlyList<SearchResult>;
    }
}

public sealed class FreeTextSearchTerm
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Normalized { get; set; } = "";
    public double Weight { get; set; } = 0.8;
}

public sealed class FreeTextSearchOptions
{
    public const string SectionName = "FreeTextSearch";

    public List<FreeTextSearchTerm> Terms { get; set; } = new();

    public int MaxResults { get; set; } = 10;
}
