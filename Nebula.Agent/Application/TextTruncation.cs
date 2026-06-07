namespace Nebula.Agent.Application;

internal static class TextTruncation
{
    public static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength].Trim();
    }

    public static string TruncateFromStart(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[^maxLength..].Trim();
    }
}
