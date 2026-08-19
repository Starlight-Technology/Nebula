using System.Text;

using Nebula.Core.Learning;

namespace Nebula.Services.Learning;

/// <summary>
/// Scans workspace documentation (README, docs folder, markdown files) and
/// persists deterministic knowledge items (concepts, code snippets, commands).
/// Idempotent per content hash; bounded file count and size.
/// </summary>
public sealed class ProjectDocumentationIndexer(
    IKnowledgeRepository store) : IProjectDocumentationIndexer
{
    private const int MaxFileCount = 15;
    private const int MaxFileBytes = 100 * 1024;
    private const int MaxItemContentChars = 6000;

    public async Task<ProjectDocumentationIndexResult> IndexAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workspaceRoot))
        {
            return new ProjectDocumentationIndexResult(
                false,
                "Workspace does not exist.",
                FilesScanned: 0,
                CreatedCount: 0,
                SkippedCount: 0);
        }

        var files = FindDocumentationFiles(workspaceRoot);
        var created = 0;
        var skipped = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadBounded(file, out var content))
            {
                continue;
            }

            var saved = await IndexFileAsync(file, content, cancellationToken);
            created += saved.created;
            skipped += saved.skipped;
        }

        var message = created + skipped == 0
            ? "Nenhuma documentacao de projeto encontrada."
            : $"Indice de documentacao atualizado: {created} criado(s), {skipped} ja conhecido(s).";
        return new ProjectDocumentationIndexResult(
            true,
            message,
            files.Count,
            created,
            skipped);
    }

    private async Task<(int created, int skipped)> IndexFileAsync(
        string file,
        string content,
        CancellationToken cancellationToken)
    {
        var domain = InferDomain(file);
        var created = 0;
        var skipped = 0;

        var preamble = ExtractPreamble(content);
        if (!string.IsNullOrWhiteSpace(preamble))
        {
            var (wasCreated, wasSkipped) = await SaveItemAsync(
                file,
                domain,
                KnowledgeItemKind.Concept,
                "Visao geral do projeto",
                Truncate(preamble),
                FirstSentences(preamble, 2),
                normalizedCommand: null,
                cancellationToken);
            created += wasCreated;
            skipped += wasSkipped;
        }

        foreach (var section in ExtractSections(content))
        {
            if (string.IsNullOrWhiteSpace(section.Value))
            {
                continue;
            }

            var (wasCreated, wasSkipped) = await SaveItemAsync(
                file,
                domain,
                KnowledgeItemKind.Concept,
                Truncate(section.Key, 120),
                Truncate(section.Value),
                FirstSentences(section.Value, 2),
                normalizedCommand: null,
                cancellationToken);
            created += wasCreated;
            skipped += wasSkipped;
        }

        foreach (var codeBlock in ExtractCodeBlocks(content))
        {
            var (wasCreated, wasSkipped) = await SaveItemAsync(
                file,
                domain,
                KnowledgeItemKind.CodeSnippet,
                $"Exemplo de codigo ({codeBlock.language})",
                Truncate(codeBlock.code),
                "Exemplo extraido da documentacao do projeto.",
                normalizedCommand: codeBlock.language,
                cancellationToken);
            created += wasCreated;
            skipped += wasSkipped;
        }

        foreach (var command in ExtractCommands(content))
        {
            var (wasCreated, wasSkipped) = await SaveItemAsync(
                file,
                domain,
                KnowledgeItemKind.Command,
                $"Comando documentado: {command}",
                command,
                "Comando documentado no projeto (uso local, validar antes de executar).",
                normalizedCommand: command,
                cancellationToken);
            created += wasCreated;
            skipped += wasSkipped;
        }

        return (created, skipped);
    }

    private async Task<(int created, int skipped)> SaveItemAsync(
        string file,
        KnowledgeDomain domain,
        KnowledgeItemKind kind,
        string title,
        string content,
        string summary,
        string? normalizedCommand,
        CancellationToken cancellationToken)
    {
        var hash = KnowledgeHash.Create(domain, title, summary, content);
        var existing = await store.FindByHashAsync(hash, cancellationToken);
        if (existing is not null)
        {
            return (0, 1);
        }

        var item = new KnowledgeItem
        {
            Id = Guid.NewGuid(),
            Domain = domain,
            Kind = kind,
            Topic = title,
            Title = title,
            Content = content,
            Summary = summary,
            Tags = "project-docs",
            NormalizedCommand = normalizedCommand,
            Language = InferLanguage(domain),
            SourceUrl = ToFileUrl(file),
            SourceType = LearningSourceType.LocalFile,
            SourceName = Path.GetFileName(file),
            RiskLevel = KnowledgeRiskLevel.Safe,
            ConfidenceScore = 0.80,
            SourceScore = 0.90,
            ClassificationConfidence = 0.75,
            SafetyScore = 1.0,
            VerificationScore = 0.5,
            FinalScore = 0.80,
            Hash = hash,
            LastSeenAt = DateTimeOffset.UtcNow,
            ObservationCount = 1,
            IsValidated = false
        };
        var sources = new List<KnowledgeSource>
        {
            new()
            {
                Id = Guid.NewGuid(),
                KnowledgeItemId = item.Id,
                Url = ToFileUrl(file),
                Title = Path.GetFileName(file),
                ProviderName = "ProjectDocumentationIndexer",
                SourceType = LearningSourceType.LocalFile,
                ExtractedContent = content.Length > 4000 ? content[..4000] : content,
                RetrievedAt = DateTimeOffset.UtcNow,
                TrustScore = 0.90
            }
        };
        var experiment = new KnowledgeExperiment
        {
            Id = Guid.NewGuid(),
            KnowledgeItemId = item.Id,
            VerificationKind = VerificationKind.SourceOnly,
            Success = false,
            ErrorCategory = "source-documentation",
            CreatedAt = DateTimeOffset.UtcNow,
            EvidenceHash = hash
        };

        await store.SaveAsync(item, sources, facts: [], experiment, cancellationToken);
        return (1, 0);
    }

    private static List<string> FindDocumentationFiles(string workspaceRoot)
    {
        var files = new List<string>();
        var rootReadme = Directory.EnumerateFiles(workspaceRoot, "README.*")
            .FirstOrDefault(value =>
                Path.GetExtension(value).Equals(".md", StringComparison.OrdinalIgnoreCase));
        if (rootReadme is not null)
        {
            files.Add(rootReadme);
        }

        foreach (var file in Directory.EnumerateFiles(workspaceRoot, "*.md", SearchOption.AllDirectories))
        {
            if (files.Count >= MaxFileCount)
            {
                break;
            }

            var relative = Path.GetRelativePath(workspaceRoot, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("node_modules" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!files.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(file);
            }
        }

        return files;
    }

    private static IEnumerable<KeyValuePair<string, string>> ExtractSections(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        string? currentHeading = null;
        var current = new StringBuilder();
        foreach (var line in lines)
        {
            var heading = TryParseHeading(line);
            if (heading is not null)
            {
                if (currentHeading is not null && current.Length > 0)
                {
                    yield return new KeyValuePair<string, string>(
                        currentHeading,
                        current.ToString().Trim());
                }

                currentHeading = heading;
                current = new StringBuilder();
                continue;
            }

            if (currentHeading is not null && !IsFence(line))
            {
                current.AppendLine(line);
            }
        }

        if (currentHeading is not null && current.Length > 0)
        {
            yield return new KeyValuePair<string, string>(
                currentHeading,
                current.ToString().Trim());
        }
    }

    private static string ExtractPreamble(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var preamble = new StringBuilder();
        foreach (var line in lines)
        {
            if (TryParseHeading(line) is not null || IsFence(line))
            {
                break;
            }

            preamble.AppendLine(line.Trim());
        }

        return preamble.ToString().Trim();
    }

    private static IEnumerable<(string language, string code)> ExtractCodeBlocks(string content)
    {
        var matches = new System.Text.RegularExpressions.Regex(
            "```(?<lang>[a-zA-Z0-9+#-]*)\\r?\\n(?<code>.*?)```",
            System.Text.RegularExpressions.RegexOptions.Singleline).Matches(content);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var code = match.Groups["code"].Value.Trim();
            if (code.Length > 0 && code.Length <= MaxItemContentChars)
            {
                yield return (match.Groups["lang"].Value, code);
            }
        }
    }

    private static IEnumerable<string> ExtractCommands(string content)
    {
        var count = 0;
        var matches = new System.Text.RegularExpressions.Regex(
            "`(?<command>[a-zA-Z0-9][^`\\r\\n]{2,120})`").Matches(content);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var command = match.Groups["command"].Value.Trim();
            command = command.TrimStart('$');
            if (command.Length < 3 ||
                command.Length > 120 ||
                command.Contains(" ", StringComparison.Ordinal) && command.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return command;
            count++;
            if (count >= 8)
            {
                yield break;
            }
        }
    }

    private static string? TryParseHeading(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("## ", StringComparison.Ordinal) ||
            trimmed.StartsWith("### ", StringComparison.Ordinal))
        {
            return trimmed[2..].Trim().TrimStart('#').Trim();
        }

        if (trimmed.StartsWith("# ", StringComparison.Ordinal) &&
            trimmed.StartsWith("# ", StringComparison.Ordinal))
        {
            return trimmed[2..].Trim();
        }

        return null;
    }

    private static bool IsFence(string line) =>
        line.Trim().StartsWith("```", StringComparison.Ordinal);

    private static KnowledgeDomain InferDomain(string file)
    {
        var path = file.ToLowerInvariant();
        if (path.Contains(".csproj", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(".cs", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("dotnet", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("csharp", StringComparison.OrdinalIgnoreCase))
        {
            return KnowledgeDomain.DotNet;
        }

        if (path.Contains(".py", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("python", StringComparison.OrdinalIgnoreCase))
        {
            return KnowledgeDomain.Python;
        }

        if (path.Contains(".ps1", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("powershell", StringComparison.OrdinalIgnoreCase))
        {
            return KnowledgeDomain.PowerShell;
        }

        return KnowledgeDomain.General;
    }

    private static string? InferLanguage(KnowledgeDomain domain) =>
        domain switch
        {
            KnowledgeDomain.DotNet => "C#",
            KnowledgeDomain.Python => "Python",
            KnowledgeDomain.PowerShell => "PowerShell",
            _ => null
        };

    private static string Truncate(string value, int max = MaxItemContentChars) =>
        value.Length <= max ? value : value[..max];

    private static string FirstSentences(string value, int count)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(
            value.Trim(),
            @"\s+",
            " ");
        if (string.IsNullOrWhiteSpace(clean))
        {
            return clean;
        }

        var sentences = clean.Split('.', '\n');
        var summary = string.Join(". ", sentences.Take(count)).Trim();
        return summary.Length <= 400 ? summary : summary[..400];
    }

    private static bool TryReadBounded(string path, out string content)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
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

    private static string ToFileUrl(string path) =>
        "file://" + path.Replace('\\', '/');
}