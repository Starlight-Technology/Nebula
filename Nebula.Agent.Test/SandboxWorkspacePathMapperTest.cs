using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class SandboxWorkspacePathMapperTest
{
    [Fact]
    public void map_must_translate_container_temp_root_to_host_root()
    {
        var mapper = new SandboxWorkspacePathMapper(
            "/tmp",
            "/home/nebula/nebula-tmp");

        var mapped = mapper.TryMap(
            "/tmp/nebula-workspace/abc",
            out var hostPath);

        Assert.True(mapped);
        Assert.Equal(
            Path.GetFullPath("/home/nebula/nebula-tmp/nebula-workspace/abc"),
            hostPath);
    }

    [Fact]
    public void map_must_reject_paths_outside_container_root()
    {
        var mapper = new SandboxWorkspacePathMapper(
            "/tmp",
            "/home/nebula/nebula-tmp");

        Assert.False(mapper.TryMap("/var/tmp/workspace", out _));
        Assert.False(mapper.TryMap("/tmp/../etc", out _));
        Assert.False(mapper.TryMap("relative/workspace", out _));
    }

    [Fact]
    public void map_must_reject_paths_that_escape_host_root()
    {
        var mapper = new SandboxWorkspacePathMapper(
            "/tmp",
            "/home/nebula/nebula-tmp");

        Assert.False(mapper.TryMap("/tmp/../tmp2/workspace", out _));
    }

    [Fact]
    public void map_must_accept_the_root_itself()
    {
        var mapper = new SandboxWorkspacePathMapper(
            "/tmp",
            "/home/nebula/nebula-tmp");

        Assert.True(mapper.TryMap("/tmp", out var hostPath));
        Assert.Equal(
            Path.GetFullPath("/home/nebula/nebula-tmp"),
            hostPath);
    }
}
