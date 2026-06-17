namespace Nebula.Core.Configuration;

public sealed class NebulaRuntimeSettings
{
    public const string DefaultLanguageCode = "pt-BR";
    public const string DefaultLanguageName = "Portugues (Brasil)";
    public const string DefaultWebResearchProvider = "Free";
    public const string DefaultAccelerationProfile = "auto";

    public string MainModel { get; set; } = string.Empty;

    public string LearningModel { get; set; } = string.Empty;

    public string WebResearchProvider { get; set; } = DefaultWebResearchProvider;

    public string AccelerationProfile { get; set; } = DefaultAccelerationProfile;

    public string ResponseLanguageCode { get; set; } = DefaultLanguageCode;

    public string ResponseLanguageName { get; set; } = DefaultLanguageName;

    public bool AutoApproveCommands { get; set; }

    public string EffectiveLearningModel =>
        string.IsNullOrWhiteSpace(LearningModel)
            ? MainModel.Trim()
            : LearningModel.Trim();

    public void Apply(NebulaRuntimeSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        MainModel = snapshot.MainModel?.Trim() ?? string.Empty;
        LearningModel = snapshot.LearningModel?.Trim() ?? string.Empty;
        WebResearchProvider = NormalizeOrDefault(
            snapshot.WebResearchProvider,
            DefaultWebResearchProvider);
        AccelerationProfile = NormalizeOrDefault(
            snapshot.AccelerationProfile,
            DefaultAccelerationProfile);
        ResponseLanguageCode = NormalizeOrDefault(
            snapshot.ResponseLanguageCode,
            DefaultLanguageCode);
        ResponseLanguageName = NormalizeOrDefault(
            snapshot.ResponseLanguageName,
            DefaultLanguageName);
        AutoApproveCommands = snapshot.AutoApproveCommands;
    }

    public NebulaRuntimeSettingsSnapshot CreateSnapshot()
    {
        return new NebulaRuntimeSettingsSnapshot
        {
            MainModel = MainModel,
            LearningModel = LearningModel,
            WebResearchProvider = WebResearchProvider,
            AccelerationProfile = AccelerationProfile,
            ResponseLanguageCode = ResponseLanguageCode,
            ResponseLanguageName = ResponseLanguageName,
            AutoApproveCommands = AutoApproveCommands
        };
    }

    public string BuildResponseLanguageInstruction()
    {
        return
            $"Answer all user-facing natural-language text in {ResponseLanguageName} " +
            $"(locale {ResponseLanguageCode}). Keep code, commands, paths, identifiers, " +
            "JSON property names, and quoted source text unchanged when translation would alter their meaning.";
    }

    private static string NormalizeOrDefault(string? value, string defaultValue)
    {
        return string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : value.Trim();
    }
}

public sealed class NebulaRuntimeSettingsSnapshot
{
    public string MainModel { get; set; } = string.Empty;

    public string LearningModel { get; set; } = string.Empty;

    public string WebResearchProvider { get; set; } =
        NebulaRuntimeSettings.DefaultWebResearchProvider;

    public string AccelerationProfile { get; set; } =
        NebulaRuntimeSettings.DefaultAccelerationProfile;

    public string ResponseLanguageCode { get; set; } =
        NebulaRuntimeSettings.DefaultLanguageCode;

    public string ResponseLanguageName { get; set; } =
        NebulaRuntimeSettings.DefaultLanguageName;

    public bool AutoApproveCommands { get; set; }
}
