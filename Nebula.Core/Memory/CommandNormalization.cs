using System.Text.RegularExpressions;

namespace Nebula.Core.Memory;

public static partial class CommandNormalization
{
    public static string Normalize(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        return WhiteSpaceRegex().Replace(
            command.Trim().ToLowerInvariant(),
            " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpaceRegex();
}
