namespace Nebula.Agent;

/// <summary>
/// Extracts JSON objects from text strings.
/// </summary>
public class JsonExtractor : IJsonExtractor
{
    /// <summary>
    /// Extracts a JSON object from a string.
    /// </summary>
    /// <param name="input">The input string containing a JSON object.</param>
    /// <returns>The extracted JSON object as a string.</returns>
    /// <exception cref="ArgumentException">Thrown when no valid JSON object is found.</exception>
    public string ExtractJsonObject(string input)
    {
        int start = input.IndexOf('{');
        int end = input.LastIndexOf('}');

        if ((start < 0) || (end < 0) || (end <= start))
            throw new ArgumentException("No valid JSON object found in the input.");

        return input.Substring(start, (end - start) + 1);
    }
}
