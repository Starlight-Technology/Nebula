namespace Nebula.Core.Safety;

public interface IScriptContentSafetyClassifier
{
    CommandClassification Classify(
        string content,
        string language,
        string? targetPath);
}

public interface IFileWriteSafetyClassifier
{
    CommandClassification Classify(string targetPath);
}
