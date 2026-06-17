using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using HtmlAgilityPack;

using Nebula.Core.Learning;

namespace Nebula.Services.Learning;

public sealed class LearningSourceReader : ILearningSourceReader
{
    private const int MaxFileBytes = 12 * 1024 * 1024;
    private const int MaxTextLength = 250_000;

    private readonly HttpClient httpClient;

    public LearningSourceReader(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Reads supported local files into learning documents.
    /// </summary>
    public async Task<IReadOnlyList<LearningSourceDocument>> ReadFilesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var documents = new List<LearningSourceDocument>();
        foreach (var filePath in filePaths.Select(NormalizeInput).Where(value => value.Length > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(filePath))
            {
                continue;
            }

            var info = new FileInfo(filePath);
            if (info.Length > MaxFileBytes)
            {
                continue;
            }

            var content = await ReadFileTextAsync(info.FullName, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            documents.Add(new LearningSourceDocument(
                info.Name,
                Truncate(content),
                info.FullName,
                nameof(LearningSourceReader),
                DateTimeOffset.UtcNow,
                LearningSourceType.LocalFile));
        }

        return documents;
    }

    /// <summary>
    /// Downloads explicit sites and extracts visible text for learning.
    /// </summary>
    public async Task<IReadOnlyList<LearningSourceDocument>> ReadSitesAsync(
        IReadOnlyList<string> siteUrls,
        CancellationToken cancellationToken = default)
    {
        var documents = new List<LearningSourceDocument>();
        foreach (var value in siteUrls.Select(NormalizeInput).Where(value => value.Length > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var text = ExtractHtmlText(content);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                documents.Add(new LearningSourceDocument(
                    ExtractTitle(content, uri),
                    Truncate(text),
                    uri.ToString(),
                    nameof(LearningSourceReader),
                    DateTimeOffset.UtcNow,
                    LearningSourceType.WebResearch));
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                continue;
            }
        }

        return documents;
    }

    private static async Task<string> ReadFileTextAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".txt" or ".md" or ".json" or ".csv" or ".log" or ".cs" or ".py" =>
                await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken),
            ".docx" => ReadDocxText(filePath),
            ".doc" => await ReadLegacyDocTextAsync(filePath, cancellationToken),
            ".pdf" => await ReadPdfTextAsync(filePath, cancellationToken),
            _ => string.Empty
        };
    }

    private static string ReadDocxText(string filePath)
    {
        using var archive = ZipFile.OpenRead(filePath);
        var entry = archive.GetEntry("word/document.xml");
        if (entry is null)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Join(
            " ",
            document.Descendants(word + "t")
                .Select(element => element.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static async Task<string> ReadLegacyDocTextAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var text = Encoding.UTF8.GetString(bytes);
        return CleanExtractedText(text);
    }

    private static async Task<string> ReadPdfTextAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var text = Encoding.Latin1.GetString(bytes);
        var values = Regex.Matches(
                text,
                @"\((?<text>(?:\\.|[^\\)])*)\)\s*(?:Tj|'|""|TJ)",
                RegexOptions.Compiled)
            .Select(match => UnescapePdfString(match.Groups["text"].Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return values.Count == 0
            ? CleanExtractedText(text)
            : CleanExtractedText(string.Join(" ", values));
    }

    private static string ExtractHtmlText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var removableNodes =
            document.DocumentNode.SelectNodes("//script|//style|//noscript");
        if (removableNodes is not null)
        {
            foreach (var node in removableNodes)
            {
                node.Remove();
            }
        }

        return WebUtility.HtmlDecode(
            CleanExtractedText(document.DocumentNode.InnerText));
    }

    private static string ExtractTitle(string html, Uri uri)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var title = WebUtility.HtmlDecode(
            document.DocumentNode.SelectSingleNode("//title")?.InnerText ?? string.Empty);
        return string.IsNullOrWhiteSpace(title)
            ? uri.Host
            : CleanExtractedText(title);
    }

    private static string UnescapePdfString(string value) =>
        value
            .Replace(@"\(", "(", StringComparison.Ordinal)
            .Replace(@"\)", ")", StringComparison.Ordinal)
            .Replace(@"\\", "\\", StringComparison.Ordinal)
            .Replace(@"\n", " ", StringComparison.Ordinal)
            .Replace(@"\r", " ", StringComparison.Ordinal)
            .Replace(@"\t", " ", StringComparison.Ordinal);

    private static string CleanExtractedText(string text)
    {
        var visible = new string(text
            .Where(character =>
                !char.IsControl(character) ||
                character is '\r' or '\n' or '\t')
            .ToArray());
        return Regex.Replace(visible, @"\s+", " ").Trim();
    }

    private static string NormalizeInput(string? value) =>
        value?.Trim().Trim('"') ?? string.Empty;

    private static string Truncate(string value) =>
        value.Length <= MaxTextLength
            ? value
            : value[..MaxTextLength];
}
