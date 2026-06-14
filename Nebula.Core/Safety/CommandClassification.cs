namespace Nebula.Core.Safety;

public sealed record CommandClassification(
    string CommandText,
    CommandIntent Intent,
    double Confidence,
    string Source,
    IReadOnlyList<string> Reasons);
