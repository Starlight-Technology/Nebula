using System.Text;
using System.Text.RegularExpressions;

namespace Nebula.Runner;

public sealed class InteractivePromptDetector
{
    private static readonly Regex PromptSuffix = new(
        @"(?i)(?:press\s+any\s+key\s+to\s+continue|press\s+enter|hit\s+enter|enter\s+to\s+continue|more\?|continue\?|proceed\?|are\s+you\s+sure\?|type\s+'yes'\s+to\s+continue|\[[yn](?:/[yn])?\]|\(y/n\)|\(yes/no\)|password:|passphrase:|username:|login:|select\s+a\s+(?:number|choice|option):|\[enter\])[\s.]*$",
        RegexOptions.CultureInvariant);

    public bool EndsWithInteractivePrompt(StringBuilder accumulated)
    {
        if (accumulated is null || accumulated.Length == 0)
        {
            return false;
        }

        var lastLine = GetLastLine(accumulated);
        return !string.IsNullOrWhiteSpace(lastLine) && PromptSuffix.IsMatch(lastLine);
    }

    private static string GetLastLine(StringBuilder builder)
    {
        for (var i = builder.Length - 1; i >= 0; i--)
        {
            if (builder[i] == '\n')
            {
                return builder.ToString(i + 1, builder.Length - i - 1);
            }
        }

        return builder.ToString();
    }
}
