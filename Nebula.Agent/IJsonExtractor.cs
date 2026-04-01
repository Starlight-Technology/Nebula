namespace Nebula.Agent;

/// <summary>
/// Responsible for extracting JSON objects from text.
/// </summary>
public interface IJsonExtractor
{
    /// <summary>
    /// Extracts a JSON object from a string.
    /// </summary>
    /// <param name="input">The input string containing a JSON object.</param>
    /// <returns>The extracted JSON object as a string.</returns>
    /// <exception cref="ArgumentException">Thrown when no valid JSON object is found.</exception>
    string ExtractJsonObject(string input);
}
