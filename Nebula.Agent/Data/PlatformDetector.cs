namespace Nebula.Agent.Data;

/// <summary>
/// Utility class for platform detection and OS-specific command validation.
/// Ensures that commands are only executed on compatible operating systems.
/// </summary>
public static class PlatformDetector
{
    /// <summary>
    /// Gets the current operating system type.
    /// </summary>
    /// <returns>"Windows", "Linux", or "macOS"</returns>
    public static string GetCurrentOsType()
    {
        if (OperatingSystem.IsWindows())
            return "Windows";
        if (OperatingSystem.IsLinux())
            return "Linux";
        if (OperatingSystem.IsMacOS())
            return "macOS";

        return "Unknown";
    }

    /// <summary>
    /// Validates whether a command is safe to execute on the current OS.
    /// </summary>
    /// <param name="osType">The OS type the command was generated for.</param>
    /// <returns>True if the command is appropriate for the current OS, false otherwise.</returns>
    public static bool IsCommandSafeForCurrentOS(string osType)
    {
        var currentOs = GetCurrentOsType();
        return osType.Equals(currentOs, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates whether a command contains only safe shell commands.
    /// For Windows: cmd.exe or PowerShell commands only.
    /// For Linux/macOS: bash or sh commands only.
    /// </summary>
    /// <param name="command">The command to validate.</param>
    /// <returns>True if the command appears to be safe, false if it contains suspicious patterns.</returns>
    public static bool IsCommandContentSafe(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var lowerCommand = command.ToLowerInvariant();

        // Dangerous patterns to prevent
        var dangerousPatterns = new[]
        {
            "rm -rf",  // Recursive delete
            "del /s", // Windows recursive delete
            "format",  // Disk format
            "cipher /w", // Windows secure deletion
            "dd if=", // Raw disk operations
            "mkfs", // Create filesystem
            "&& rm", // Chained deletion
            "; rm", // Chained deletion
            "| rm", // Piped deletion
            "&& del", // Windows chained deletion
            "; del", // Windows chained deletion
        };

        return !dangerousPatterns.Any(pattern => lowerCommand.Contains(pattern));
    }
}
