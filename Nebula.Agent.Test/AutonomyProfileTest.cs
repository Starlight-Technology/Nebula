using Nebula.Core.Configuration;
using Nebula.Core.Execution;

namespace Nebula.Agent.Test;

public sealed class AutonomyProfileTest
{
    [Theory]
    [InlineData(AutonomyProfile.Conservative, 0)]
    [InlineData(AutonomyProfile.Standard, 2)]
    [InlineData(AutonomyProfile.Autonomous, 4)]
    [InlineData(AutonomyProfile.Lab, 6)]
    public void get_default_categories_must_return_expected_count(AutonomyProfile profile, int expectedCount)
    {
        var categories = NebulaRuntimeSettings.GetDefaultCategoriesForProfile(profile);
        Assert.Equal(expectedCount, categories.Count);
    }

    [Theory]
    [InlineData(AutonomyProfile.Conservative, false)]
    [InlineData(AutonomyProfile.Standard, false)]
    [InlineData(AutonomyProfile.Autonomous, true)]
    [InlineData(AutonomyProfile.Lab, true)]
    public void get_default_auto_approve_must_match_profile(AutonomyProfile profile, bool expected)
    {
        Assert.Equal(expected, NebulaRuntimeSettings.GetDefaultAutoApproveForProfile(profile));
    }

    [Theory]
    [InlineData(AutonomyProfile.Lab, SandboxMode.Docker)]
    [InlineData(AutonomyProfile.Standard, SandboxMode.Disabled)]
    [InlineData(AutonomyProfile.Conservative, SandboxMode.Disabled)]
    public void get_default_sandbox_must_match_profile(AutonomyProfile profile, SandboxMode expected)
    {
        Assert.Equal(expected, NebulaRuntimeSettings.GetDefaultSandboxForProfile(profile));
    }

    [Theory]
    [InlineData(AutonomyProfile.Conservative, SandboxMode.Docker, SandboxMode.Disabled, SandboxMode.Docker)]
    [InlineData(AutonomyProfile.Standard, SandboxMode.Docker, SandboxMode.Disabled, SandboxMode.Docker)]
    [InlineData(AutonomyProfile.Autonomous, SandboxMode.Docker, SandboxMode.Disabled, SandboxMode.Disabled)]
    [InlineData(AutonomyProfile.Lab, SandboxMode.Docker, SandboxMode.Disabled, SandboxMode.Disabled)]
    [InlineData(AutonomyProfile.Conservative, SandboxMode.Disabled, SandboxMode.Docker, SandboxMode.Docker)]
    [InlineData(AutonomyProfile.Standard, SandboxMode.Disabled, SandboxMode.Disabled, SandboxMode.Disabled)]
    public void resolve_sandbox_mode_must_apply_profile_floor(
        AutonomyProfile profile,
        SandboxMode serverFloor,
        SandboxMode requested,
        SandboxMode expected)
    {
        var resolved = NebulaRuntimeSettings.ResolveSandboxMode(
            profile,
            serverFloor,
            requested);

        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("mcr.microsoft.com/powershell:lts", true)]
    [InlineData("alpine:3.20", true)]
    [InlineData("ghcr.io/org/image@sha256:abc123", true)]
    [InlineData("--privileged alpine", false)]
    [InlineData("alpine; rm -rf /", false)]
    [InlineData("-alpine", false)]
    [InlineData("alpine image", false)]
    [InlineData("", false)]
    public void sandbox_image_validation_must_reject_docker_flags_and_shell_metacharacters(
        string image,
        bool expected)
    {
        Assert.Equal(expected, NebulaRuntimeSettings.IsValidSandboxImage(image));
    }

    [Fact]
    public void invalid_sandbox_image_must_normalize_to_default()
    {
        Assert.Equal(
            NebulaRuntimeSettings.DefaultSandboxImage,
            NebulaRuntimeSettings.NormalizeSandboxImage("--privileged alpine"));
    }

    [Fact]
    public void apply_must_not_overwrite_server_sandbox_floor()
    {
        var settings = new NebulaRuntimeSettings(SandboxMode.Docker);
        settings.Apply(new NebulaRuntimeSettingsSnapshot
        {
            SandboxMode = SandboxMode.Disabled,
            SandboxConfigured = true
        });

        Assert.Equal(SandboxMode.Docker, settings.SandboxModeFloor);
        Assert.Equal(SandboxMode.Disabled, settings.SandboxMode);
    }

    [Fact]
    public void create_snapshot_must_mark_sandbox_as_configured()
    {
        var settings = new NebulaRuntimeSettings
        {
            SandboxMode = SandboxMode.Docker
        };

        var snapshot = settings.CreateSnapshot();

        Assert.True(snapshot.SandboxConfigured);
        Assert.Equal(SandboxMode.Docker, snapshot.SandboxMode);
    }

    [Fact]
    public void snapshot_roundtrip_must_preserve_autonomy_profile()
    {
        var settings = new NebulaRuntimeSettings
        {
            AutonomyProfile = AutonomyProfile.Autonomous,
            UserId = "user-123",
            MainModel = "m",
            WorkspaceRoot = "/tmp/ws"
        };

        var snapshot = settings.CreateSnapshot();
        var restored = new NebulaRuntimeSettings();
        restored.Apply(snapshot);

        Assert.Equal(AutonomyProfile.Autonomous, restored.AutonomyProfile);
        Assert.Equal("user-123", restored.UserId);
    }

    [Theory]
    [InlineData(AutonomyProfile.Conservative, 0)]
    [InlineData(AutonomyProfile.Standard, 2)]
    [InlineData(AutonomyProfile.Autonomous, 4)]
    [InlineData(AutonomyProfile.Lab, 6)]
    public void effective_categories_must_include_profile_defaults(
        AutonomyProfile profile,
        int expectedCount)
    {
        var settings = new NebulaRuntimeSettings { AutonomyProfile = profile };

        Assert.Equal(expectedCount, settings.EffectiveAutoApproveCategories.Count);
    }

    [Fact]
    public void effective_categories_must_merge_explicit_categories_without_duplicates()
    {
        var settings = new NebulaRuntimeSettings
        {
            AutonomyProfile = AutonomyProfile.Standard,
            AutoApproveCategories = ["network-access", "read-only"]
        };

        var categories = settings.EffectiveAutoApproveCategories;

        Assert.Contains("network-access", categories);
        Assert.Contains("read-only", categories);
        Assert.Contains("write-local", categories);
        Assert.Equal(
            categories.Count,
            categories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(AutonomyProfile.Conservative, false)]
    [InlineData(AutonomyProfile.Standard, false)]
    [InlineData(AutonomyProfile.Autonomous, true)]
    [InlineData(AutonomyProfile.Lab, true)]
    public void effective_auto_approve_must_use_profile_default_when_not_configured(
        AutonomyProfile profile,
        bool expected)
    {
        var settings = new NebulaRuntimeSettings { AutonomyProfile = profile };

        Assert.Equal(expected, settings.EffectiveAutoApproveCommands);
    }

    [Fact]
    public void explicit_disabled_auto_approve_must_suppress_profile_categories()
    {
        var settings = new NebulaRuntimeSettings
        {
            AutonomyProfile = AutonomyProfile.Autonomous,
            AutoApproveCommands = false,
            AutoApproveCommandsConfigured = true,
            AutoApproveCategories = ["read-only"]
        };

        var categories = settings.EffectiveAutoApproveCategories;

        Assert.Single(categories);
        Assert.Equal("read-only", categories[0]);
    }

    [Fact]
    public void effective_auto_approve_must_respect_explicit_value()
    {
        var disabled = new NebulaRuntimeSettings
        {
            AutonomyProfile = AutonomyProfile.Autonomous,
            AutoApproveCommands = false,
            AutoApproveCommandsConfigured = true
        };
        var enabled = new NebulaRuntimeSettings
        {
            AutonomyProfile = AutonomyProfile.Conservative,
            AutoApproveCommands = true,
            AutoApproveCommandsConfigured = true
        };

        Assert.False(disabled.EffectiveAutoApproveCommands);
        Assert.True(enabled.EffectiveAutoApproveCommands);
    }

    [Fact]
    public void snapshot_roundtrip_must_preserve_auto_approve_configured_flag()
    {
        var settings = new NebulaRuntimeSettings
        {
            AutoApproveCommands = false,
            AutoApproveCommandsConfigured = true
        };

        var restored = new NebulaRuntimeSettings();
        restored.Apply(settings.CreateSnapshot());

        Assert.True(restored.AutoApproveCommandsConfigured);
        Assert.False(restored.EffectiveAutoApproveCommands);
    }

    [Fact]
    public void apply_must_handle_null_snapshot_categories()
    {
        var settings = new NebulaRuntimeSettings
        {
            AutonomyProfile = AutonomyProfile.Lab
        };
        var snapshot = new NebulaRuntimeSettingsSnapshot
        {
            AutonomyProfile = AutonomyProfile.Conservative,
            AutoApproveCategories = null!
        };

        settings.Apply(snapshot);

        Assert.Equal(AutonomyProfile.Conservative, settings.AutonomyProfile);
        Assert.Empty(settings.AutoApproveCategories);
    }
}
