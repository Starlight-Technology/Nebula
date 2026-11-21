using Nebula.Core.Agent;
using Nebula.Core.Configuration;
using Nebula.Core.Interactions;

namespace Nebula.Agent.Test;

public sealed class LocalFirstArchitectureTest
{
    [Fact]
    public void default_runtime_policy_must_be_local_preferred()
    {
        var settings = new NebulaRuntimeSettings();

        Assert.Equal(ProviderMode.LocalPreferred, settings.ProviderMode);
        Assert.True(settings.AllowsLocal);
        Assert.True(settings.CanUseRemoteFallback);
    }

    [Fact]
    public void local_only_mode_must_restrict_remote_usage()
    {
        var settings = new NebulaRuntimeSettings
        {
            ProviderMode = ProviderMode.LocalOnly
        };

        Assert.True(settings.AllowsLocal);
        Assert.False(settings.AllowsRemote);
        Assert.False(settings.CanUseRemoteFallback);
    }

    [Fact]
    public void agent_registry_must_prefer_local_agents_for_local_only_mode()
    {
        var registry = new AgentRegistry();

        var best = registry.ResolveBestFor(AgentCapability.Coding, ProviderMode.LocalOnly);

        Assert.NotNull(best);
        Assert.Equal("coding-local", best!.Id);
        Assert.Contains(AgentCapability.Coding, best.Capabilities);
    }

    [Fact]
    public void profile_defaults_should_keep_local_first_behaviour()
    {
        Assert.Equal(ProviderMode.LocalOnly, NebulaRuntimeSettings.GetDefaultProviderModeForProfile(AutonomyProfile.Conservative));
        Assert.Equal(ProviderMode.LocalPreferred, NebulaRuntimeSettings.GetDefaultProviderModeForProfile(AutonomyProfile.Standard));
        Assert.Equal(ProviderMode.LocalPreferred, NebulaRuntimeSettings.GetDefaultProviderModeForProfile(AutonomyProfile.Autonomous));
        Assert.Equal(ProviderMode.RemoteAllowed, NebulaRuntimeSettings.GetDefaultProviderModeForProfile(AutonomyProfile.Lab));
    }

    [Fact]
    public void runtime_snapshot_should_expose_provider_mode_for_explicit_local_first_control()
    {
        var snapshot = new NebulaRuntimeSettingsSnapshot();

        Assert.Equal(ProviderMode.LocalPreferred, snapshot.ProviderMode);
        snapshot.ProviderMode = ProviderMode.LocalOnly;
        Assert.Equal(ProviderMode.LocalOnly, snapshot.ProviderMode);
    }

    [Fact]
    public void provider_selector_should_respect_local_first_policy()
    {
        var selector = new AgentProviderSelector();

        var localOnly = selector.Select(AgentCapability.Coding, new NebulaRuntimeSettings
        {
            ProviderMode = ProviderMode.LocalOnly
        });

        var preferred = selector.Select(AgentCapability.Coding, new NebulaRuntimeSettings
        {
            ProviderMode = ProviderMode.LocalPreferred
        });

        var remoteOnly = selector.Select(AgentCapability.Coding, new NebulaRuntimeSettings
        {
            ProviderMode = ProviderMode.RemoteOnly
        });

        Assert.NotNull(localOnly);
        Assert.True(localOnly!.IsLocal);
        Assert.NotNull(preferred);
        Assert.True(preferred!.IsLocal);
        Assert.NotNull(remoteOnly);
        Assert.False(remoteOnly!.IsLocal);
    }

    [Fact]
    public void user_message_should_preserve_provider_mode_for_runtime_selection()
    {
        var message = new UserMessage(
            "Crie um script local",
            InteractionMode.Agent,
            WorkspaceRoot: "/tmp/workspace",
            IsDryRun: true,
            ProviderMode: ProviderMode.LocalOnly);

        Assert.Equal(ProviderMode.LocalOnly, message.ProviderMode);
        Assert.True(message.IsDryRun);
    }
}
