using Nebula.Core.Configuration;
using Nebula.Core.Execution;

namespace Nebula.Agent.Test;

public sealed class AutonomyProfileTest
{
    [Theory]
    [InlineData(AutonomyProfile.Conservative, 0)]
    [InlineData(AutonomyProfile.Standard, 2)]
    [InlineData(AutonomyProfile.Autonomous, 4)]
    [InlineData(AutonomyProfile.Laboratory, 6)]
    public void get_default_categories_must_return_expected_count(AutonomyProfile profile, int expectedCount)
    {
        var categories = NebulaRuntimeSettings.GetDefaultCategoriesForProfile(profile);
        Assert.Equal(expectedCount, categories.Count);
    }

    [Theory]
    [InlineData(AutonomyProfile.Conservative, false)]
    [InlineData(AutonomyProfile.Standard, false)]
    [InlineData(AutonomyProfile.Autonomous, true)]
    [InlineData(AutonomyProfile.Laboratory, true)]
    public void get_default_auto_approve_must_match_profile(AutonomyProfile profile, bool expected)
    {
        Assert.Equal(expected, NebulaRuntimeSettings.GetDefaultAutoApproveForProfile(profile));
    }

    [Theory]
    [InlineData(AutonomyProfile.Laboratory, SandboxMode.Docker)]
    [InlineData(AutonomyProfile.Standard, SandboxMode.Disabled)]
    [InlineData(AutonomyProfile.Conservative, SandboxMode.Disabled)]
    public void get_default_sandbox_must_match_profile(AutonomyProfile profile, SandboxMode expected)
    {
        Assert.Equal(expected, NebulaRuntimeSettings.GetDefaultSandboxForProfile(profile));
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

    [Fact]
    public void apply_must_handle_null_snapshot_categories()
    {
        var settings = new NebulaRuntimeSettings
        {
            AutonomyProfile = AutonomyProfile.Laboratory
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
