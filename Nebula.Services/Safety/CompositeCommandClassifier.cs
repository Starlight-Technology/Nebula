using Nebula.Core.Safety;

namespace Nebula.Services.Safety;

public sealed class CompositeCommandClassifier(
    ICommandClassifier deterministicClassifier,
    ICommandClassifier mlClassifier,
    double deterministicConfidenceThreshold = 0.95) : ICommandClassifier
{
    public async Task<CommandClassification> ClassifyAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        var deterministic = await deterministicClassifier.ClassifyAsync(commandText, cancellationToken);
        if (deterministic.Intent != CommandIntent.Unknown
            && deterministic.Confidence >= deterministicConfidenceThreshold)
        {
            return deterministic;
        }

        var ml = await mlClassifier.ClassifyAsync(commandText, cancellationToken);
        if (ml.Intent == CommandIntent.Unknown)
        {
            return deterministic with
            {
                Source = nameof(CompositeCommandClassifier),
                Reasons = [.. deterministic.Reasons, .. ml.Reasons]
            };
        }

        return ml with
        {
            Source = $"{nameof(CompositeCommandClassifier)}({ml.Source})",
            Reasons =
            [
                .. deterministic.Reasons,
                .. ml.Reasons,
                "ML.NET is advisory; the policy engine applies the final authorization rules."
            ]
        };
    }
}
