using Nebula.Core.Configuration;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class SandboxRuntimeLocatorTest
{
    [Fact]
    public void auto_must_prefer_docker()
    {
        var locator = new SandboxRuntimeLocator(name =>
            name is "docker" or "podman");

        var runtime = locator.Locate(SandboxContainerRuntime.Auto);

        Assert.NotNull(runtime);
        Assert.Equal("docker", runtime.Name);
        Assert.Equal("docker", runtime.Executable);
        Assert.False(runtime.RelabelWorkspace);
    }

    [Fact]
    public void auto_must_fall_back_to_podman()
    {
        var locator = new SandboxRuntimeLocator(name => name == "podman");

        var runtime = locator.Locate(SandboxContainerRuntime.Auto);

        Assert.NotNull(runtime);
        Assert.Equal("podman", runtime.Name);
        Assert.Equal("podman", runtime.Executable);
        Assert.Equal(OperatingSystem.IsLinux(), runtime.RelabelWorkspace);
    }

    [Fact]
    public void explicit_podman_must_not_fall_back_to_docker()
    {
        var locator = new SandboxRuntimeLocator(name => name == "docker");

        var runtime = locator.Locate(SandboxContainerRuntime.Podman);

        Assert.Null(runtime);
    }

    [Fact]
    public void explicit_docker_must_not_fall_back_to_podman()
    {
        var locator = new SandboxRuntimeLocator(name => name == "podman");

        var runtime = locator.Locate(SandboxContainerRuntime.Docker);

        Assert.Null(runtime);
    }

    [Fact]
    public void missing_runtime_must_return_null()
    {
        var locator = new SandboxRuntimeLocator(_ => false);

        var runtime = locator.Locate(SandboxContainerRuntime.Auto);

        Assert.Null(runtime);
    }
}
