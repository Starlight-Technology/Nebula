using Nebula.Core.Safety;

namespace Nebula.Runner;

public sealed class SandboxWorkspacePathMapper
{
    public SandboxWorkspacePathMapper(string containerRoot, string hostRoot)
    {
        if (string.IsNullOrWhiteSpace(containerRoot))
        {
            throw new ArgumentException(
                "Container workspace root is required.",
                nameof(containerRoot));
        }

        if (string.IsNullOrWhiteSpace(hostRoot))
        {
            throw new ArgumentException(
                "Host workspace root is required.",
                nameof(hostRoot));
        }

        ContainerRoot = Path.GetFullPath(containerRoot);
        HostRoot = Path.GetFullPath(hostRoot);
    }

    public string ContainerRoot { get; }

    public string HostRoot { get; }

    public bool TryMap(string containerPath, out string hostPath)
    {
        hostPath = string.Empty;
        if (string.IsNullOrWhiteSpace(containerPath) ||
            !Path.IsPathRooted(containerPath))
        {
            return false;
        }

        if (!PathContainment.IsUnder(containerPath, ContainerRoot))
        {
            return false;
        }

        var relative = Path.GetRelativePath(
            ContainerRoot,
            Path.GetFullPath(containerPath));
        var candidate = relative == "."
            ? HostRoot
            : Path.GetFullPath(Path.Combine(HostRoot, relative));

        if (!PathContainment.IsUnder(candidate, HostRoot))
        {
            return false;
        }

        hostPath = candidate;
        return true;
    }
}
