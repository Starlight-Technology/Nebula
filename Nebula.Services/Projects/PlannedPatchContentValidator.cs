using System.Text.Json;

using Nebula.Core.Projects;

namespace Nebula.Services.Projects;

/// <summary>
/// Detects planned patch files whose content was clearly cut off by the model
/// (for example an HTML page without a closing tag). Incomplete content must be
/// sent back to the planner instead of being written to disk.
/// </summary>
public static class PlannedPatchContentValidator
{
    private static readonly string[] HtmlExtensions = [".html", ".htm"];

    private static readonly string[] JsonExtensions = [".json"];

    private static readonly string[] CssExtensions = [".css"];

    private static readonly string[] BraceExtensions =
        [".css", ".js", ".mjs", ".cjs", ".ts"];

    private static readonly string[] ScriptExtensions =
    [
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx",
        ".py", ".cs", ".java", ".go", ".rb", ".php", ".sh", ".ps1"
    ];

    private static readonly string[] PlaceholderPhrases =
    [
        "placeholder", "lorem ipsum", "code for", "code to", "script for",
        "content for", "conteudo da", "conteúdo da", "conteudo do",
        "conteúdo do", "codigo da", "código da", "codigo do", "código do",
        "implement the", "implemente", "complete calculator code"
    ];

    public static string? Validate(IReadOnlyList<PlannedPatchFile> files)
    {
        foreach (var file in files)
        {
            var reason = ValidateFile(file);
            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    public static string? ValidateFile(PlannedPatchFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Content))
        {
            return $"O conteudo de '{file.RelativePath}' esta vazio; " +
                   "reenvie o arquivo completo.";
        }

        var extension = Path.GetExtension(file.RelativePath);
        if (HtmlExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return ValidateHtml(file);
        }

        if (JsonExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var _ = JsonDocument.Parse(file.Content);
            }
            catch (JsonException)
            {
                return $"O JSON de '{file.RelativePath}' esta invalido ou " +
                       "truncado; reenvie o arquivo completo.";
            }
        }

        if (CssExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
            !LooksLikeCssContent(file.Content))
        {
            return BuildPlaceholderReason(file.RelativePath);
        }

        if (BraceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
            !HasBalancedBraces(file.Content))
        {
            return $"O conteudo de '{file.RelativePath}' parece truncado " +
                   "(chaves desbalanceadas); reenvie o arquivo completo.";
        }

        if (ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
            IsPlaceholderDescription(file.Content) &&
            !HasCodeStructure(file.Content))
        {
            return BuildPlaceholderReason(file.RelativePath);
        }

        return null;
    }

    private static bool IsPlaceholderDescription(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 200)
        {
            return false;
        }

        return PlaceholderPhrases.Any(phrase =>
            trimmed.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasCodeStructure(string content)
    {
        return content.IndexOfAny(['{', '}', '(', ')', '[', ']', ';', '=']) >= 0;
    }

    private static bool LooksLikeCssContent(string content)
    {
        return content.Contains('{') ||
               content.Contains('}') ||
               (content.Contains(':') && content.Contains(';')) ||
               content
                   .Split('\n')
                   .Any(line => line.TrimStart().StartsWith("@", StringComparison.Ordinal));
    }

    private static string BuildPlaceholderReason(string path)
    {
        return $"O conteudo de '{path}' parece um placeholder ou descricao, nao " +
               "codigo real (por exemplo, 'HTML and JS code for calculator'). " +
               "Envie o codigo completo e funcional do arquivo.";
    }

    private static string? ValidateHtml(PlannedPatchFile file)
    {
        var content = file.Content;
        var opensHtml = content.Contains("<html", StringComparison.OrdinalIgnoreCase);
        var closesHtml = content.Contains("</html>", StringComparison.OrdinalIgnoreCase);
        if (opensHtml && !closesHtml)
        {
            return $"O HTML de '{file.RelativePath}' parece truncado " +
                   "(falta </html>); reenvie o arquivo completo.";
        }

        var opensBody = content.Contains("<body", StringComparison.OrdinalIgnoreCase);
        var closesBody = content.Contains("</body>", StringComparison.OrdinalIgnoreCase);
        if (opensBody && !closesBody)
        {
            return $"O HTML de '{file.RelativePath}' parece truncado " +
                   "(falta </body>); reenvie o arquivo completo.";
        }

        var hasTag = content.Contains('<') &&
                     content.Contains('>') &&
                     (content.Contains("</") ||
                      content.Contains("/>") ||
                      content.Contains("<!"));
        if (!hasTag)
        {
            return BuildPlaceholderReason(file.RelativePath);
        }

        return null;
    }

    private static bool HasBalancedBraces(string content)
    {
        var depth = 0;
        var quote = '\0';
        var escaped = false;
        var lineComment = false;
        var blockComment = false;

        for (var index = 0; index < content.Length; index++)
        {
            var current = content[index];

            if (lineComment)
            {
                if (current == '\n')
                {
                    lineComment = false;
                }

                continue;
            }

            if (blockComment)
            {
                if (current == '*' &&
                    index + 1 < content.Length &&
                    content[index + 1] == '/')
                {
                    blockComment = false;
                    index++;
                }

                continue;
            }

            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current == '/' && index + 1 < content.Length)
            {
                if (content[index + 1] == '/')
                {
                    lineComment = true;
                    index++;
                    continue;
                }

                if (content[index + 1] == '*')
                {
                    blockComment = true;
                    index++;
                    continue;
                }
            }

            if (current is '\'' or '"' or '`')
            {
                quote = current;
                continue;
            }

            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth < 0)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }
}
