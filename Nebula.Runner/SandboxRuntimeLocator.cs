using Nebula.Core.Configuration;

namespace Nebula.Runner;

public sealed record SandboxRuntimeInfo(
    string Name,
    string Executable,
    bool RelabelWorkspace);

public interface ISandboxRuntimeLocator
{
    SandboxRuntimeInfo? Locate(SandboxContainerRuntime preference);
}

public sealed class SandboxRuntimeLocator : ISandboxRuntimeLocator
{
    private readonly Func<string, bool> isAvailable;

    public SandboxRuntimeLocator()
        : this(IsAvailableOnPath)
    {
    }

    public SandboxRuntimeLocator(Func<string, bool> isAvailable)
    {
        this.isAvailable = isAvailable ??
            throw new ArgumentNullException(nameof(isAvailable));
    }

    public SandboxRuntimeInfo? Locate(SandboxContainerRuntime preference)
    {
        return preference switch
        {
            SandboxContainerRuntime.Docker => Create("docker"),
            SandboxContainerRuntime.Podman => Create("podman"),
            _ => Create("docker") ?? Create("podman")
        };
    }

    private SandboxRuntimeInfo? Create(string name)
    {
        if (!isAvailable(name))
        {
            return null;
        }

        return new SandboxRuntimeInfo(
            name,
            name,
            name == "podman" && OperatingSystem.IsLinux());
    }

    private static bool IsAvailableOnPath(string executable)
    {
        if (OperatingSystem.IsBrowser())
        {
            return false;
        }

        if (Path.IsPathRooted(executable))
        {
            return IsExecutable(executable);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extensions = GetExecutableExtensions();
        foreach (var rawDirectory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                if (IsExecutable(Path.Combine(directory, executable + extension)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string[] GetExecutableExtensions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [string.Empty];
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return [".exe", ".cmd", ".bat"];
        }

        return pathExt.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return true;
        }
    }
}
