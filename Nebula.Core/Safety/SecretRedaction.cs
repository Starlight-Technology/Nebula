using System.Text.RegularExpressions;

namespace Nebula.Core.Safety;

/// <summary>
/// Masks secret-like values before they reach logs or the UI.
/// </summary>
public static class SecretRedaction
{
    private static readonly Regex PrivateKeyPattern = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TokenPattern = new(
        @"\b(?:sk-[A-Za-z0-9_-]{16,}|xox[baprs]-[A-Za-z0-9-]{8,}|ghp_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{20,}|github_pat_[A-Za-z0-9_]{20,})\b",
        RegexOptions.Compiled);

    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.Compiled);

    private static readonly Regex KeyValuePattern = new(
        @"([""']?(?:api[_-]?key|apikey|secret|password|passwd|token|client[_-]?secret|access[_-]?key)[""']?\s*[:=]\s*(?:bearer\s+)?)(?:""[^""]*""|'[^']*'|[^""',\s}]{6,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AuthorizationPattern = new(
        @"(authorization\s*[:=]\s*bearer\s+)[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string Mask = "***";

    public static string? Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = PrivateKeyPattern.Replace(text, Mask);
        result = TokenPattern.Replace(result, Mask);
        result = JwtPattern.Replace(result, Mask);
        result = KeyValuePattern.Replace(result, match =>
            $"{match.Groups[1].Value}{Mask}");
        return AuthorizationPattern.Replace(result, match =>
            $"{match.Groups[1].Value}{Mask}");
    }
}
