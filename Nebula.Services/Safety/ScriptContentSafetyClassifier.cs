using System.Text.RegularExpressions;

using Nebula.Core.Safety;

namespace Nebula.Services.Safety;

public sealed class ScriptContentSafetyClassifier : IScriptContentSafetyClassifier
{
    private static readonly string[] SensitiveTerms =
    [
        ".env", ".ssh", "id_rsa", "id_ed25519", "access_token",
        "auth_token", "api_key", "apikey", "credentials", "password"
    ];

    private readonly IFileWriteSafetyClassifier fileWriteClassifier;

    public ScriptContentSafetyClassifier(
        IFileWriteSafetyClassifier? fileWriteClassifier = null)
    {
        this.fileWriteClassifier =
            fileWriteClassifier ?? new FileWriteSafetyClassifier();
    }

    public CommandClassification Classify(
        string content,
        string language,
        string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Result(
                content,
                CommandIntent.Blocked,
                1,
                "Empty script content is not executable.");
        }

        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            var pathClassification = fileWriteClassifier.Classify(targetPath);
            if (pathClassification.Intent is
                CommandIntent.Blocked or CommandIntent.NeedsApproval)
            {
                return pathClassification with
                {
                    CommandText = content,
                    Source = nameof(ScriptContentSafetyClassifier),
                    Reasons =
                    [
                        .. pathClassification.Reasons,
                        "Script content inherits the target-path decision."
                    ]
                };
            }
        }

        var normalizedLanguage = NormalizeLanguage(language, targetPath);
        var normalized = content.ToLowerInvariant();

        if (SensitiveTerms.Any(normalized.Contains))
        {
            return Result(
                content,
                CommandIntent.DataExfiltration,
                0.99,
                "The content references credentials, tokens, .env, or SSH material.");
        }

        return normalizedLanguage switch
        {
            "python" => ClassifyPython(content, normalized),
            "csharp" => ClassifyCSharp(content, normalized),
            "json" or "markdown" or "text" =>
                ClassifyDocument(content, normalized),
            _ => Result(
                content,
                CommandIntent.NeedsApproval,
                0.50,
                $"No deterministic script-content rules exist for language '{normalizedLanguage}'.")
        };
    }

    private static CommandClassification ClassifyPython(
        string content,
        string normalized)
    {
        if (DestructivePythonRegex.IsMatch(normalized))
        {
            return Result(
                content,
                CommandIntent.Blocked,
                1,
                "The Python content invokes a destructive system or recursive-delete operation.");
        }

        if (RiskyPythonRegex.IsMatch(normalized))
        {
            return Result(
                content,
                CommandIntent.NeedsApproval,
                0.99,
                "The Python content can execute processes, evaluate code, access the network, or manipulate the filesystem.");
        }

        if (normalized.Contains("import os", StringComparison.Ordinal) &&
            ContainsOsUseOtherThanGetCwd(normalized))
        {
            return Result(
                content,
                CommandIntent.NeedsApproval,
                0.98,
                "Python os usage is only allowed automatically for os.getcwd().");
        }

        if (!HasOnlyAllowedPythonConstructs(content))
        {
            return Result(
                content,
                CommandIntent.NeedsApproval,
                0.97,
                "The Python content contains a function, import, or language construct outside the automatic allowlist.");
        }

        return Result(
            content,
            CommandIntent.SafeWriteLocal,
            0.99,
            "The Python content is limited to local output, data values, json.dumps, encode/decode, and approved imports.");
    }

    private static CommandClassification ClassifyCSharp(
        string content,
        string normalized)
    {
        if (DestructiveCSharpRegex.IsMatch(normalized))
        {
            return Result(
                content,
                CommandIntent.Blocked,
                1,
                "The C# content deletes files/directories or starts a dangerous process.");
        }

        if (RiskyCSharpRegex.IsMatch(normalized))
        {
            return Result(
                content,
                CommandIntent.NeedsApproval,
                0.99,
                "The C# content starts processes, accesses the network, uses dangerous reflection, or reads sensitive storage.");
        }

        if (!HasOnlyAllowedCSharpConstructs(content))
        {
            return Result(
                content,
                CommandIntent.NeedsApproval,
                0.97,
                "The C# content contains an API call outside the simple console-program allowlist.");
        }

        return Result(
            content,
            CommandIntent.SafeWriteLocal,
            0.99,
            "The C# content is a simple local console program.");
    }

    private static CommandClassification ClassifyDocument(
        string content,
        string normalized)
    {
        if (PromptInjectionRegex.IsMatch(normalized))
        {
            return Result(
                content,
                CommandIntent.Blocked,
                0.99,
                "The document contains an explicit prompt-injection attempt against the agent.");
        }

        return Result(
            content,
            CommandIntent.SafeWriteLocal,
            0.99,
            "JSON, Markdown, and text content is allowed inside an approved local path.");
    }

    private static bool ContainsOsUseOtherThanGetCwd(string normalized)
    {
        var withoutAllowedUse = normalized
            .Replace("import os", string.Empty, StringComparison.Ordinal)
            .Replace("os.getcwd()", string.Empty, StringComparison.Ordinal);
        return Regex.IsMatch(withoutAllowedUse, @"\bos\.", RegexOptions.IgnoreCase);
    }

    private static bool HasOnlyAllowedPythonConstructs(string content)
    {
        foreach (var line in content.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("import ", StringComparison.OrdinalIgnoreCase))
            {
                var imported = line["import ".Length..].Trim();
                if (imported is not ("json" or "sys" or "os"))
                {
                    return false;
                }
            }

            var codeLine = PythonStringLiteralRegex.Replace(line, "\"\"");
            if (Regex.IsMatch(
                codeLine,
                @"\b(?:from|def|class|lambda|with|while|for|try|except|finally|yield)\b",
                RegexOptions.IgnoreCase))
            {
                return false;
            }
        }

        var codeWithoutStrings = PythonStringLiteralRegex.Replace(content, "\"\"");
        foreach (Match match in PythonCallRegex.Matches(codeWithoutStrings))
        {
            var call = match.Groups["call"].Value;
            if (call is "print" or "json.dumps" or "os.getcwd")
            {
                continue;
            }

            if (call.EndsWith(".encode", StringComparison.Ordinal) ||
                call.EndsWith(".decode", StringComparison.Ordinal))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool HasOnlyAllowedCSharpConstructs(string content)
    {
        foreach (Match match in CSharpCallRegex.Matches(content))
        {
            var call = match.Groups["call"].Value;
            if (call.Equals(
                "Console.WriteLine",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (call.Equals("Main", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return false;
        }

        return !Regex.IsMatch(
            content,
            @"\b(?:System\.IO|File\.|Directory\.|Process\.|HttpClient|WebClient|Socket|Reflection|Assembly\.Load)\b",
            RegexOptions.IgnoreCase);
    }

    private static string NormalizeLanguage(string language, string? targetPath)
    {
        var value = language.Trim().ToLowerInvariant();
        if (value is "py" or "python")
        {
            return "python";
        }

        if (value is "cs" or "c#" or "csharp")
        {
            return "csharp";
        }

        if (value is "md" or "markdown")
        {
            return "markdown";
        }

        if (value is "txt" or "text")
        {
            return "text";
        }

        if (value == "json")
        {
            return "json";
        }

        return Path.GetExtension(targetPath ?? string.Empty).ToLowerInvariant() switch
        {
            ".py" => "python",
            ".cs" => "csharp",
            ".json" => "json",
            ".md" => "markdown",
            ".txt" => "text",
            _ => value
        };
    }

    private static CommandClassification Result(
        string text,
        CommandIntent intent,
        double confidence,
        string reason) =>
        new(
            text,
            intent,
            confidence,
            nameof(ScriptContentSafetyClassifier),
            [reason]);

    private static readonly Regex DestructivePythonRegex = new(
        @"\bos\.system\s*\(|\bshutil\.rmtree\s*\(|rm\s+-rf|remove-item.+-recurse|del\s+/s",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RiskyPythonRegex = new(
        @"\bsubprocess\b|\beval\s*\(|\bexec\s*\(|\bsocket\b|\brequests\b|\burllib\b|\bshutil\.(?!rmtree)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DestructiveCSharpRegex = new(
        @"\bfile\.delete\s*\(|\bdirectory\.delete\s*\(|process\.start\s*\([^)]*(?:rm\s+-rf|del\s+/s|format)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RiskyCSharpRegex = new(
        @"\bprocess\.start\s*\(|\bhttpclient\b|\bwebclient\b|\bsocket\b|\bassembly\.load\b|\btypeof\s*\([^)]*\)\.assembly|\bgetenvironmentvariable\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PromptInjectionRegex = new(
        @"(?:ignore|disregard|bypass|override).{0,40}(?:system prompt|developer instructions|safety policy|agent rules)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PythonCallRegex = new(
        @"(?<![\w.])(?<call>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex PythonStringLiteralRegex = new(
        @"(?s)("""".*?""""|'''.*?'''|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')",
        RegexOptions.Compiled);

    private static readonly Regex CSharpCallRegex = new(
        @"(?<![\w.])(?<call>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*\(",
        RegexOptions.Compiled);
}
