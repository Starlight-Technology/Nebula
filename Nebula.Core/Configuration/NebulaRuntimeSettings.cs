using Nebula.Core.Execution;

namespace Nebula.Core.Configuration;

public enum AutonomyProfile
{
    Conservative = 0,
    Standard = 1,
    Autonomous = 2,
    Lab = 3
}

public enum ProviderMode
{
    LocalOnly = 0,
    LocalPreferred = 1,
    RemoteAllowed = 2,
    RemoteOnly = 3
}

public enum SandboxContainerRuntime
{
    Auto = 0,
    Docker = 1,
    Podman = 2
}

public sealed class NebulaRuntimeSettings
{
    public const string DefaultLanguageCode = "pt-BR";
    public const string DefaultLanguageName = "Portugues (Brasil)";
    public const string DefaultWebResearchProvider = "Free";
    public const string DefaultAccelerationProfile = "auto";
    public const string DefaultSandboxImage = "mcr.microsoft.com/powershell:lts";
    public const int DefaultSmokeTestTimeoutSeconds = 10;

    public NebulaRuntimeSettings()
    {
    }

    public NebulaRuntimeSettings(SandboxMode sandboxModeFloor)
    {
        SandboxModeFloor = sandboxModeFloor;
        SandboxMode = sandboxModeFloor;
    }

    public string MainModel { get; set; } = string.Empty;

    public string LearningModel { get; set; } = string.Empty;

    public string WebResearchProvider { get; set; } = DefaultWebResearchProvider;

    public string AccelerationProfile { get; set; } = DefaultAccelerationProfile;

    public string ResponseLanguageCode { get; set; } = DefaultLanguageCode;

    public string ResponseLanguageName { get; set; } = DefaultLanguageName;

    public bool AutoApproveCommands { get; set; }

    public bool AutoApproveCommandsConfigured { get; set; }

    public List<string> AutoApproveCategories { get; set; } = [];

    public string WorkspaceRoot { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public AutonomyProfile AutonomyProfile { get; set; } = AutonomyProfile.Standard;

    public ProviderMode ProviderMode { get; set; } = ProviderMode.LocalPreferred;

    public bool AllowsLocal => ProviderMode is not ProviderMode.RemoteOnly;

    public bool AllowsRemote => ProviderMode is ProviderMode.RemoteAllowed or ProviderMode.RemoteOnly;

    public bool CanUseRemoteFallback => ProviderMode is ProviderMode.LocalPreferred or ProviderMode.RemoteAllowed or ProviderMode.RemoteOnly;

    public SandboxMode SandboxModeFloor { get; init; } = SandboxMode.Disabled;

    public SandboxContainerRuntime SandboxRuntime { get; init; } = SandboxContainerRuntime.Auto;

    public string SandboxSidecarUrl { get; init; } = string.Empty;

    public string SandboxSidecarToken { get; init; } = string.Empty;

    public bool RequireDeterministicVerification { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 300;

    public int ScriptTimeoutSeconds { get; set; } = 300;

    public int LlmTimeoutSeconds { get; set; } = 300;

    public int MaxVerificationRetries { get; set; } = 2;

    public int MaxConsecutiveErrorRetries { get; set; } = 3;

    public bool EnableResultValidation { get; set; } = true;

    public bool EnableEnvironmentValidation { get; set; } = true;

    public bool SmokeTestEnabled { get; set; } = true;

    public int SmokeTestTimeoutSeconds { get; set; } = DefaultSmokeTestTimeoutSeconds;

    public SandboxMode SandboxMode { get; set; } = SandboxMode.Disabled;

    public string SandboxImage { get; set; } = DefaultSandboxImage;

    public long SandboxMemoryLimitMb { get; set; }

    public double SandboxCpuLimit { get; set; }

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

    public IReadOnlyList<string> EffectiveAutoApproveCategories =>
        AutoApproveCategories
            .Concat(
                AutoApproveCommandsConfigured && !AutoApproveCommands
                    ? []
                    : GetDefaultCategoriesForProfile(AutonomyProfile))
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool EffectiveAutoApproveCommands =>
        AutoApproveCommandsConfigured
            ? AutoApproveCommands
            : AutoApproveCommands ||
                GetDefaultAutoApproveForProfile(AutonomyProfile);

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
        AutoApproveCommandsConfigured = snapshot.AutoApproveCommandsConfigured;
        AutoApproveCategories = snapshot.AutoApproveCategories?.ToList() ?? [];
        WorkspaceRoot = snapshot.WorkspaceRoot?.Trim() ?? string.Empty;
        UserId = snapshot.UserId?.Trim() ?? string.Empty;
        AutonomyProfile = snapshot.AutonomyProfile;
        ProviderMode = snapshot.ProviderMode;
        RequireDeterministicVerification = snapshot.RequireDeterministicVerification;
        CommandTimeoutSeconds = Math.Max(0, snapshot.CommandTimeoutSeconds);
        ScriptTimeoutSeconds = Math.Max(0, snapshot.ScriptTimeoutSeconds);
        MaxVerificationRetries = Math.Max(0, snapshot.MaxVerificationRetries);
        LlmTimeoutSeconds = Math.Max(1, snapshot.LlmTimeoutSeconds);
        MaxConsecutiveErrorRetries = Math.Max(1, snapshot.MaxConsecutiveErrorRetries);
        EnableResultValidation = snapshot.EnableResultValidation;
        EnableEnvironmentValidation = snapshot.EnableEnvironmentValidation;
        SmokeTestEnabled = snapshot.SmokeTestEnabled;
        SmokeTestTimeoutSeconds = Math.Max(1, snapshot.SmokeTestTimeoutSeconds);
        SandboxMode = Enum.IsDefined(snapshot.SandboxMode)
            ? snapshot.SandboxMode
            : SandboxMode.Disabled;
        SandboxImage = NormalizeSandboxImage(snapshot.SandboxImage);
        SandboxMemoryLimitMb = Math.Max(0, snapshot.SandboxMemoryLimitMb);
        SandboxCpuLimit = Math.Max(0, snapshot.SandboxCpuLimit);
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
            AutoApproveCommandsConfigured = AutoApproveCommandsConfigured,
            AutoApproveCategories = AutoApproveCategories.ToList(),
            WorkspaceRoot = WorkspaceRoot,
            UserId = UserId,
            AutonomyProfile = AutonomyProfile,
            ProviderMode = ProviderMode,
            RequireDeterministicVerification = RequireDeterministicVerification,
            CommandTimeoutSeconds = CommandTimeoutSeconds,
            ScriptTimeoutSeconds = ScriptTimeoutSeconds,
            MaxVerificationRetries = MaxVerificationRetries,
            LlmTimeoutSeconds = LlmTimeoutSeconds,
            MaxConsecutiveErrorRetries = MaxConsecutiveErrorRetries,
            EnableResultValidation = EnableResultValidation,
            EnableEnvironmentValidation = EnableEnvironmentValidation,
            SmokeTestEnabled = SmokeTestEnabled,
            SmokeTestTimeoutSeconds = SmokeTestTimeoutSeconds,
            SandboxMode = SandboxMode,
            SandboxConfigured = true,
            SandboxImage = NormalizeSandboxImage(SandboxImage),
            SandboxMemoryLimitMb = SandboxMemoryLimitMb,
            SandboxCpuLimit = SandboxCpuLimit,
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

    public static IReadOnlyList<string> GetDefaultCategoriesForProfile(AutonomyProfile profile)
    {
        return profile switch
        {
            AutonomyProfile.Conservative => [],
            AutonomyProfile.Standard => ["read-only", "write-local"],
            AutonomyProfile.Autonomous => ["read-only", "write-local", "execute-local", "network-access"],
            AutonomyProfile.Lab => ["read-only", "write-local", "execute-local", "network-access", "package-install", "privileged-operation"],
            _ => []
        };
    }

    public static bool GetDefaultAutoApproveForProfile(AutonomyProfile profile)
    {
        return profile is AutonomyProfile.Autonomous or AutonomyProfile.Lab;
    }

    public static SandboxMode GetDefaultSandboxForProfile(AutonomyProfile profile)
    {
        return profile is AutonomyProfile.Lab ? SandboxMode.Docker : SandboxMode.Disabled;
    }

    public static SandboxMode ResolveSandboxMode(
        AutonomyProfile profile,
        SandboxMode serverFloor,
        SandboxMode requestedMode)
    {
        if (profile is AutonomyProfile.Autonomous or AutonomyProfile.Lab)
        {
            return requestedMode;
        }

        return serverFloor == SandboxMode.Docker
            ? SandboxMode.Docker
            : requestedMode;
    }

    public static SandboxContainerRuntime ParseSandboxRuntime(string? value)
    {
        return Enum.TryParse<SandboxContainerRuntime>(
                   value,
                   ignoreCase: true,
                   out var runtime) &&
               Enum.IsDefined(runtime)
            ? runtime
            : SandboxContainerRuntime.Auto;
    }

    public static bool IsValidSandboxImage(string? image)
    {
        var value = image?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            return false;
        }

        if (!IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or '/' or ':' or '@');
    }

    public static string NormalizeSandboxImage(string? image) =>
        IsValidSandboxImage(image)
            ? image!.Trim()
            : DefaultSandboxImage;

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    public static ProviderMode GetDefaultProviderModeForProfile(AutonomyProfile profile)
    {
        return profile switch
        {
            AutonomyProfile.Conservative => ProviderMode.LocalOnly,
            AutonomyProfile.Standard => ProviderMode.LocalPreferred,
            AutonomyProfile.Autonomous => ProviderMode.LocalPreferred,
            AutonomyProfile.Lab => ProviderMode.RemoteAllowed,
            _ => ProviderMode.LocalPreferred
        };
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

    public bool AutoApproveCommandsConfigured { get; set; }

    public List<string> AutoApproveCategories { get; set; } = [];

    public string WorkspaceRoot { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public AutonomyProfile AutonomyProfile { get; set; } = AutonomyProfile.Standard;

    public ProviderMode ProviderMode { get; set; } = ProviderMode.LocalPreferred;

    public bool RequireDeterministicVerification { get; set; } = true;

    public int CommandTimeoutSeconds { get; set; } = 300;

    public int ScriptTimeoutSeconds { get; set; } = 300;

    public int MaxVerificationRetries { get; set; } = 2;

    public int LlmTimeoutSeconds { get; set; } = 300;

    public int MaxConsecutiveErrorRetries { get; set; } = 3;

    public bool EnableResultValidation { get; set; } = true;

    public bool EnableEnvironmentValidation { get; set; } = true;

    public bool SmokeTestEnabled { get; set; } = true;

    public int SmokeTestTimeoutSeconds { get; set; } =
        NebulaRuntimeSettings.DefaultSmokeTestTimeoutSeconds;

    public SandboxMode SandboxMode { get; set; } = SandboxMode.Disabled;

    public bool SandboxConfigured { get; set; }

    public string SandboxScope { get; set; } = string.Empty;

    public string SandboxImage { get; set; } = NebulaRuntimeSettings.DefaultSandboxImage;

    public long SandboxMemoryLimitMb { get; set; }

    public double SandboxCpuLimit { get; set; }

    public bool CompactMode { get; set; }

    public bool ScreenshotEnabled { get; set; } = true;

    public double ScreenshotDiffThresholdPercent { get; set; } = 0.1;

    public List<string> ScreenshotViewports { get; set; } = new() { "1920x1080", "1366x768", "375x667" };

    public int ScreenshotCaptureDelayMs { get; set; } = 1000;

    public string ScreenshotViewportsText { get; set; } = "1920x1080\n1366x768\n375x667";
}
