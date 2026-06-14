namespace Nebula.Core.Safety;

public interface ICommandClassifier
{
    Task<CommandClassification> ClassifyAsync(
        string commandText,
        CancellationToken cancellationToken = default);
}
