using Microsoft.Extensions.Configuration;

using Nebula.Core.Configuration;
using Nebula.Sandbox;

namespace Nebula.Agent.Test.Sandbox;

public sealed class SandboxOptionsTest
{
    [Fact]
    public void from_configuration_reads_values_and_clamps_limits()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SANDBOX_TOKEN"] = "  secret  ",
                ["SANDBOX_RUNTIME"] = "Podman",
                ["SANDBOX_CONTAINER_TEMP_ROOT"] = "/workspace",
                ["SANDBOX_HOST_TEMP_ROOT"] = "/host/workspace",
                ["SANDBOX_MAX_TIMEOUT_SECONDS"] = "9999",
                ["SANDBOX_MAX_OUTPUT_CHARS"] = "1"
            })
            .Build();

        var options = SandboxOptions.FromConfiguration(configuration);

        Assert.Equal("secret", options.Token);
        Assert.Equal(SandboxContainerRuntime.Podman, options.Runtime);
        Assert.Equal("/workspace", options.ContainerTempRoot);
        Assert.Equal("/host/workspace", options.HostTempRoot);
        Assert.Equal(3600, options.MaxTimeoutSeconds);
        Assert.Equal(1000, options.MaxOutputChars);
    }

    [Fact]
    public void from_configuration_uses_safe_defaults()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = SandboxOptions.FromConfiguration(configuration);

        Assert.Equal(string.Empty, options.Token);
        Assert.Equal(SandboxContainerRuntime.Auto, options.Runtime);
        Assert.Equal("/tmp", options.ContainerTempRoot);
        Assert.Equal(string.Empty, options.HostTempRoot);
        Assert.Equal(300, options.MaxTimeoutSeconds);
        Assert.Equal(1_000_000, options.MaxOutputChars);
    }
}
