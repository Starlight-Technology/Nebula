using Nebula.Core.Execution;

namespace Nebula.Core.Configuration;

public enum AutonomyProfile
{
    Conservative = 0,
    Standard = 1,
    Autonomous = 2,
    Laboratory = 3
}

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

    public List<string> AutoApproveCategories { get; set; } = [];

    public string WorkspaceRoot { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public AutonomyProfile AutonomyProfile { get; set; } = AutonomyProfile.Standard;

    public bool RequireDeterministicVerification { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 300;

    public int ScriptTimeoutSeconds { get; set; } = 300;

    public int MaxVerificationRetries { get; set; } = 2;

    public SandboxMode SandboxMode { get; set; } = SandboxMode.Disabled;

    public string SandboxImage { get; set; } = "mcr.microsoft.com/powershell:lts";

    public long SandboxMemoryLimitMb { get; set; }

    public double SandboxCpuLimit { get; set; }

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
        AutoApproveCategories = snapshot.AutoApproveCategories?.ToList() ?? [];
        WorkspaceRoot = snapshot.WorkspaceRoot?.Trim() ?? string.Empty;
        UserId = snapshot.UserId?.Trim() ?? string.Empty;
        AutonomyProfile = snapshot.AutonomyProfile;
        RequireDeterministicVerification = snapshot.RequireDeterministicVerification;
        CommandTimeoutSeconds = Math.Max(0, snapshot.CommandTimeoutSeconds);
        ScriptTimeoutSeconds = Math.Max(0, snapshot.ScriptTimeoutSeconds);
        MaxVerificationRetries = Math.Max(0, snapshot.MaxVerificationRetries);
        SandboxMode = snapshot.SandboxMode;
        SandboxImage = NormalizeOrDefault(snapshot.SandboxImage, "mcr.microsoft.com/powershell:lts");
        SandboxMemoryLimitMb = Math.Max(0, snapshot.SandboxMemoryLimitMb);
        SandboxCpuLimit = Math.Max(0, snapshot.SandboxCpuLimit);
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
            AutoApproveCommands = AutoApproveCommands,
            AutoApproveCategories = AutoApproveCategories.ToList(),
            WorkspaceRoot = WorkspaceRoot,
            UserId = UserId,
            AutonomyProfile = AutonomyProfile,
            RequireDeterministicVerification = RequireDeterministicVerification,
            CommandTimeoutSeconds = CommandTimeoutSeconds,
            ScriptTimeoutSeconds = ScriptTimeoutSeconds,
            MaxVerificationRetries = MaxVerificationRetries,
            SandboxMode = SandboxMode,
            SandboxImage = SandboxImage,
            SandboxMemoryLimitMb = SandboxMemoryLimitMb,
            SandboxCpuLimit = SandboxCpuLimit
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

    public static IReadOnlyList<string> GetDefaultCategoriesForProfile(AutonomyProfile profile)
    {
        return profile switch
        {
            AutonomyProfile.Conservative => [],
            AutonomyProfile.Standard => ["read-only", "write-local"],
            AutonomyProfile.Autonomous => ["read-only", "write-local", "execute-local", "network-access"],
            AutonomyProfile.Laboratory => ["read-only", "write-local", "execute-local", "network-access", "package-install", "privileged-operation"],
            _ => []
        };
    }

    public static bool GetDefaultAutoApproveForProfile(AutonomyProfile profile)
    {
        return profile is AutonomyProfile.Autonomous or AutonomyProfile.Laboratory;
    }

    public static SandboxMode GetDefaultSandboxForProfile(AutonomyProfile profile)
    {
        return profile is AutonomyProfile.Laboratory ? SandboxMode.Docker : SandboxMode.Disabled;
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

    public List<string> AutoApproveCategories { get; set; } = [];

    public string WorkspaceRoot { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public AutonomyProfile AutonomyProfile { get; set; } = AutonomyProfile.Standard;

    public bool RequireDeterministicVerification { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 300;

    public int ScriptTimeoutSeconds { get; set; } = 300;

    public int MaxVerificationRetries { get; set; } = 2;

    public SandboxMode SandboxMode { get; set; } = SandboxMode.Disabled;

    public string SandboxImage { get; set; } = "mcr.microsoft.com/powershell:lts";

    public long SandboxMemoryLimitMb { get; set; }

    public double SandboxCpuLimit { get; set; }
}
