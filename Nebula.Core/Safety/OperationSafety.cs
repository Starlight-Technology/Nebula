namespace Nebula.Core.Safety;

public interface IScriptContentSafetyClassifier
{
    CommandClassification Classify(
        string content,
        string language,
        string? targetPath,
        string? workspaceRoot = null,
        string? controlledTempRoot = null);
}

public interface IFileWriteSafetyClassifier
{
    CommandClassification Classify(string targetPath, string? workspaceRoot = null, string? controlledTempRoot = null);
}
