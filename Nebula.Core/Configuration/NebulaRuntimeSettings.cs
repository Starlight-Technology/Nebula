using Nebula.Core.Execution;

namespace Nebula.Core.Configuration;

public enum AutonomyProfile
{
    Conservative,
    Standard,
    Autonomous,
    Lab
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

    public bool RequireDeterministicVerification { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 300;

    public int ScriptTimeoutSeconds { get; set; } = 300;

    public int MaxVerificationRetries { get; set; } = 2;

    public SandboxMode SandboxMode { get; set; } = SandboxMode.Disabled;

    public string SandboxImage { get; set; } = "mcr.microsoft.com/powershell:lts";

    public long SandboxMemoryLimitMb { get; set; }

    public double SandboxCpuLimit { get; set; }

    public AutonomyProfile AutonomyProfile { get; set; } = AutonomyProfile.Standard;

    public bool CompactMode { get; set; }

    public bool ScreenshotEnabled { get; set; } = true;

    public double ScreenshotDiffThresholdPercent { get; set; } = 0.1;

    public List<string> ScreenshotViewports { get; set; } = new() { "1920x1080", "1366x768", "375x667" };

    public int ScreenshotCaptureDelayMs { get; set; } = 1000;

    public string ScreenshotViewportsText =>
        ScreenshotViewports != null && ScreenshotViewports.Count > 0
            ? string.Join("\n", ScreenshotViewports)
            : "1920x1080\n1366x768\n375x667";

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
        RequireDeterministicVerification = snapshot.RequireDeterministicVerification;
        CommandTimeoutSeconds = Math.Max(0, snapshot.CommandTimeoutSeconds);
        ScriptTimeoutSeconds = Math.Max(0, snapshot.ScriptTimeoutSeconds);
        MaxVerificationRetries = Math.Max(0, snapshot.MaxVerificationRetries);
        SandboxMode = snapshot.SandboxMode;
        SandboxImage = NormalizeOrDefault(snapshot.SandboxImage, "mcr.microsoft.com/powershell:lts");
        SandboxMemoryLimitMb = Math.Max(0, snapshot.SandboxMemoryLimitMb);
        SandboxCpuLimit = Math.Max(0, snapshot.SandboxCpuLimit);
        AutonomyProfile = snapshot.AutonomyProfile;
        CompactMode = snapshot.CompactMode;
        ScreenshotEnabled = snapshot.ScreenshotEnabled;
        ScreenshotDiffThresholdPercent = snapshot.ScreenshotDiffThresholdPercent;
        ScreenshotViewports = snapshot.ScreenshotViewports?.ToList() ?? new() { "1920x1080", "1366x768", "375x667" };
        ScreenshotCaptureDelayMs = snapshot.ScreenshotCaptureDelayMs;
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
            RequireDeterministicVerification = RequireDeterministicVerification,
            CommandTimeoutSeconds = CommandTimeoutSeconds,
            ScriptTimeoutSeconds = ScriptTimeoutSeconds,
            MaxVerificationRetries = MaxVerificationRetries,
            SandboxMode = SandboxMode,
            SandboxImage = SandboxImage,
            SandboxMemoryLimitMb = SandboxMemoryLimitMb,
            SandboxCpuLimit = SandboxCpuLimit,
            AutonomyProfile = AutonomyProfile,
            CompactMode = CompactMode,
            ScreenshotEnabled = ScreenshotEnabled,
            ScreenshotDiffThresholdPercent = ScreenshotDiffThresholdPercent,
            ScreenshotViewports = ScreenshotViewports.ToList(),
            ScreenshotCaptureDelayMs = ScreenshotCaptureDelayMs,
            ScreenshotViewportsText = ScreenshotViewportsText
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

    public List<string> AutoApproveCategories { get; set; } = [];

    public string WorkspaceRoot { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public bool RequireDeterministicVerification { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 300;

    public int ScriptTimeoutSeconds { get; set; } = 300;

    public int MaxVerificationRetries { get; set; } = 2;

    public SandboxMode SandboxMode { get; set; } = SandboxMode.Disabled;

    public string SandboxImage { get; set; } = "mcr.microsoft.com/powershell:lts";

    public long SandboxMemoryLimitMb { get; set; }

    public double SandboxCpuLimit { get; set; }

    public AutonomyProfile AutonomyProfile { get; set; } = AutonomyProfile.Standard;

    public bool CompactMode { get; set; }

    public bool ScreenshotEnabled { get; set; } = true;

    public double ScreenshotDiffThresholdPercent { get; set; } = 0.1;

    public List<string> ScreenshotViewports { get; set; } = new() { "1920x1080", "1366x768", "375x667" };

    public int ScreenshotCaptureDelayMs { get; set; } = 1000;

    public string ScreenshotViewportsText { get; set; } = "1920x1080\n1366x768\n375x667";
}
