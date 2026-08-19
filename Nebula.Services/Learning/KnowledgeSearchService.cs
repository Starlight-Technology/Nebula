using System.Text;
using System.Text.RegularExpressions;

using Nebula.Core.Learning;

namespace Nebula.Services.Learning;

/// <summary>
/// Deterministic token-overlap search over stored knowledge and workspace files.
/// No embeddings: ranking uses query-token frequency in title/summary/content/tags.
/// </summary>
public sealed class KnowledgeSearchService(
    IKnowledgeStore store) : IKnowledgeSearchService
{
    private const int MaxQueryTokens = 6;
    private const int MaxFileCount = 25;
    private const int MaxFileBytes = 200 * 1024;
    private const int SnippetLength = 220;

    private static readonly string[] SearchableExtensions =
    [
        ".md", ".txt", ".cs", ".py", ".js", ".ts", ".json", ".razor",
        ".csproj", ".fsproj", ".xml", ".yaml", ".yml", ".toml"
    ];

    private static readonly string[] SkippedDirectories =
    [
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build"
    ];

    public async Task<IReadOnlyList<KnowledgeSearchHit>> SearchKnowledgeAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var tokens = Tokenize(query).Take(MaxQueryTokens).ToList();
        if (tokens.Count == 0)
        {
            return [];
        }

        var candidates = await FindCandidatesAsync(query, tokens, cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        var scored = candidates
            .Select(result => ScoreKnowledge(result.Item, tokens))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Item.FinalScore)
            .Take(Math.Max(1, maxResults))
            .ToList();
        return scored;
    }

    public async Task<IReadOnlyList<ProjectFileSearchHit>> SearchProjectAsync(
        string workspaceRoot,
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (!Directory.Exists(workspaceRoot))
        {
            return [];
        }

        var tokens = Tokenize(query).Take(MaxQueryTokens).ToList();
        if (tokens.Count == 0)
        {
            return [];
        }

        var files = FindCandidateFiles(workspaceRoot);
        var hits = new List<ProjectFileSearchHit>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadBounded(file, out var content))
            {
                continue;
            }

            var score = ScoreText(content, tokens);
            if (score <= 0)
            {
                continue;
            }

            hits.Add(new ProjectFileSearchHit(
                file,
                score,
                BuildSnippet(content, tokens)));
            if (hits.Count >= Math.Max(1, maxResults) * 4)
            {
                break;
            }
        }

        var result = hits
            .OrderByDescending(hit => hit.Score)
            .Take(Math.Max(1, maxResults))
            .ToList();
        return result;
    }

    private async Task<IReadOnlyList<KnowledgeLookupResult>> FindCandidatesAsync(
        string query,
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<Guid, KnowledgeLookupResult>();
        var queries = new List<string> { query.Trim() };
        queries.AddRange(tokens);
        foreach (var candidateQuery in queries)
        {
            var results = await store.FindDetailsAsync(
                candidateQuery,
                minimumScore: 0,
                cancellationToken);
            foreach (var result in results)
            {
                byId[result.Item.Id] = result;
            }
        }

        return byId.Values.ToList();
    }

    private static KnowledgeSearchHit ScoreKnowledge(
        KnowledgeItem item,
        IReadOnlyList<string> tokens)
    {
        var titleScore = ScoreText(item.Title, tokens) * 3.0;
        var summaryScore = ScoreText(item.Summary, tokens) * 2.0;
        var contentScore = ScoreText(item.Content, tokens);
        var tagsScore = ScoreText(item.Tags, tokens) * 3.0;
        var examplesScore = ScoreText(item.Examples, tokens);
        var commandScore = string.IsNullOrWhiteSpace(item.NormalizedCommand)
            ? 0.0
            : ScoreText(item.NormalizedCommand, tokens) * 4.0;

        var score = titleScore + summaryScore + contentScore +
                    tagsScore + examplesScore + commandScore;
        if (score <= 0)
        {
            return new KnowledgeSearchHit(item, 0, string.Empty);
        }

        var searchable = string.Join(
            " ",
            item.Title,
            item.Summary,
            item.Content,
            item.Examples,
            item.Tags);
        return new KnowledgeSearchHit(item, score, BuildSnippet(searchable, tokens));
    }

    private static List<string> FindCandidateFiles(string workspaceRoot)
    {
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     workspaceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (files.Count >= MaxFileCount)
            {
                break;
            }

            var relative = Path.GetRelativePath(workspaceRoot, file);
            if (SkippedDirectories.Any(dir =>
                    relative.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!SearchableExtensions.Contains(
                    Path.GetExtension(file),
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetSize(file, out var size) && size <= MaxFileBytes)
            {
                files.Add(file);
            }
        }

        return files;
    }

    private static double ScoreText(string text, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var textTokens = Tokenize(text).ToList();
        if (textTokens.Count == 0)
        {
            return 0;
        }

        var frequencies = new Dictionary<string, int>();
        foreach (var token in textTokens)
        {
            frequencies[token] = frequencies.GetValueOrDefault(token) + 1;
        }

        var score = 0.0;
        foreach (var token in tokens)
        {
            var frequency = frequencies.GetValueOrDefault(token);
            if (frequency > 0)
            {
                score += Math.Min(3, frequency);
            }
        }

        return score;
    }

    private static string BuildSnippet(string text, IReadOnlyList<string> tokens)
    {
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var token = tokens.FirstOrDefault(value =>
            normalized.Contains(value, StringComparison.OrdinalIgnoreCase));
        if (token is null)
        {
            return normalized.Length <= SnippetLength
                ? normalized
                : normalized[..SnippetLength];
        }

        var index = normalized.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, index - 60);
        var length = Math.Min(SnippetLength, normalized.Length - start);
        var snippet = normalized.Substring(start, length).Trim();
        return start > 0 ? $"…{snippet}" : snippet;
    }

    private static IEnumerable<string> Tokenize(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_+.#\-]{2,}")
            .Select(match => match.Value)
            .Where(value => value is not "the" and not "and" and not "for" and not "com");

    private static bool TryReadBounded(string path, out string content)
    {
        try
        {
            if (!TryGetSize(path, out var size) || size > MaxFileBytes)
            {
                content = string.Empty;
                return false;
            }

            content = File.ReadAllText(path, Encoding.UTF8);
            return true;
        }
        catch
        {
            content = string.Empty;
            return false;
        }
    }

    private static bool TryGetSize(string path, out long size)
    {
        try
        {
            size = new FileInfo(path).Length;
            return true;
        }
        catch
        {
            size = 0;
            return false;
        }
    }
}