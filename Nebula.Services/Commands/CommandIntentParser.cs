using System.Text.RegularExpressions;

using Nebula.Core.Commands;

namespace Nebula.Services.Commands;

public sealed partial class CommandIntentParser : ICommandIntentParser
{
    public CommandRequest Parse(
        string userText,
        string? rawCommand,
        string workingDirectory)
    {
        var source = string.Join(
            ' ',
            new[] { userText, rawCommand }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        var drive = TryParseWindowsDrive(source);
        var path = drive is null
            ? TryParsePath(source, userText, rawCommand)
            : $"{drive}:\\";

        return new CommandRequest(
            userText,
            path,
            drive,
            rawCommand);
    }

    private static string? TryParseWindowsDrive(string source)
    {
        var explicitDrive = ExplicitDriveRegex().Match(source);
        if (explicitDrive.Success)
        {
            return explicitDrive.Groups["drive"].Value.ToUpperInvariant();
        }

        var contextualDrive = ContextualDriveRegex().Match(source);
        return contextualDrive.Success
            ? contextualDrive.Groups["drive"].Value.ToUpperInvariant()
            : null;
    }

    private static string? TryParsePath(
        string source,
        string userText,
        string? rawCommand)
    {
        var windowsPath = WindowsPathRegex().Match(source);
        if (windowsPath.Success)
        {
            return windowsPath.Groups["path"].Value.Trim('"', '\'');
        }

        var unixPath = UnixPathRegex().Match(source);
        if (unixPath.Success)
        {
            return unixPath.Groups["path"].Value.Trim('"', '\'');
        }

        var quotedPath = QuotedPathRegex().Match(source);
        if (quotedPath.Success)
        {
            return quotedPath.Groups["path"].Value;
        }

        var commandPath = CommandPathRegex().Match(rawCommand ?? string.Empty);
        if (commandPath.Success)
        {
            return commandPath.Groups["path"].Value.Trim();
        }

        var naturalPath = NaturalPathRegex().Match(userText);
        return naturalPath.Success
            ? naturalPath.Groups["path"].Value.Trim()
            : null;
    }

    [GeneratedRegex(
        @"(?<![a-z0-9])(?<drive>[a-z]):(?:[\\/])?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitDriveRegex();

    [GeneratedRegex(
        @"(?:\bunidade\b|\bdrive\b|\bdir\b|\bls\b|\barquivos\b|\bfiles\b|\bdo\b|\bda\b|\bno\b|\bna\b)\s+(?:(?:unidade|drive)\s+)?(?<drive>[a-z])(?::(?:[\\/])?)?(?![a-z])",
        RegexOptions.IgnoreCase)]
    private static partial Regex ContextualDriveRegex();

    [GeneratedRegex(
        @"(?<path>(?:[a-z]:[\\/]|\\\\)[^""'\r\n;&|]*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(
        @"(?:^|\s)(?<path>/[^\s""';|&]*)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"[""'](?<path>[^""']+)[""']")]
    private static partial Regex QuotedPathRegex();

    [GeneratedRegex(
        @"^\s*(?:ls|dir|mkdir|md)\s+(?:-[a-z]+\s+)*(?<path>[^;&|]+?)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CommandPathRegex();

    [GeneratedRegex(
        @"\b(?:pasta|diret[oó]rio|folder|directory)\s+(?:chamad[ao]\s+)?(?<path>[\w.\-\\/]+)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NaturalPathRegex();
}
