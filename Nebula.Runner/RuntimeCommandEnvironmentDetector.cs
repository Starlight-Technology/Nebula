using Nebula.Core.Commands;

namespace Nebula.Runner;

public sealed class RuntimeCommandEnvironmentDetector : IRuntimeCommandEnvironmentDetector
{
    public RuntimeCommandEnvironment Detect(string workingDirectory)
    {
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);

        if (OperatingSystem.IsWindows())
        {
            return new RuntimeCommandEnvironment(
                OperatingSystemKind.Windows,
                GetPowerShellExecutable() is not null ? ShellKind.PowerShell : ShellKind.Cmd,
                fullWorkingDirectory);
        }

        if (OperatingSystem.IsLinux())
        {
            return new RuntimeCommandEnvironment(
                OperatingSystemKind.Linux,
                File.Exists("/bin/bash") ? ShellKind.Bash : ShellKind.Sh,
                fullWorkingDirectory);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new RuntimeCommandEnvironment(
                OperatingSystemKind.MacOS,
                File.Exists("/bin/bash") ? ShellKind.Bash : ShellKind.Sh,
                fullWorkingDirectory);
        }

        return new RuntimeCommandEnvironment(
            OperatingSystemKind.Unknown,
            ShellKind.Unknown,
            fullWorkingDirectory);
    }

    internal static string? GetPowerShellExecutable()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            var windowsPowerShell = Path.Combine(
                systemRoot,
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (File.Exists(windowsPowerShell))
            {
                return "powershell.exe";
            }
        }

        if (FindOnPath("powershell.exe") is not null)
        {
            return "powershell.exe";
        }

        return FindOnPath("pwsh.exe") is not null
            ? "pwsh.exe"
            : null;
    }

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), executable))
            .FirstOrDefault(File.Exists);
    }
}
