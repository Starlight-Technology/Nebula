using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nebula.Agent.Domain;

public sealed class ExecutionHistoryEntry
{
    public string Command { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = string.Empty;

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public int ExitCode { get; init; }

    public bool Success { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string EnvironmentFingerprint { get; init; } = string.Empty;

    public string FileFingerprint { get; init; } = string.Empty;

    public string ErrorSignature { get; init; } = string.Empty;
}

public sealed class ExecutionHistory
{
    public const int DefaultCapacity = 12;

    private readonly int capacity;
    private readonly List<ExecutionHistoryEntry> entries = [];

    public ExecutionHistory(int capacity = DefaultCapacity)
    {
        this.capacity = Math.Max(1, capacity);
    }

    public IReadOnlyList<ExecutionHistoryEntry> Entries => entries;

    public void Add(ExecutionHistoryEntry entry)
    {
        entries.Add(entry);
        if (entries.Count > capacity)
        {
            entries.RemoveRange(0, entries.Count - capacity);
        }
    }

    public IReadOnlyList<ExecutionHistoryEntry> FindRecentFailures(string command)
    {
        var normalizedCommand = CommandDeduplication.NormalizeCommand(command);
        return entries
            .Where(entry =>
                !entry.Success &&
                CommandDeduplication.NormalizeCommand(entry.Command) == normalizedCommand)
            .Reverse()
            .ToList();
    }

    public int CountFailures(string errorSignature)
    {
        if (string.IsNullOrWhiteSpace(errorSignature))
        {
            return 0;
        }

        return entries.Count(entry =>
            !entry.Success &&
            string.Equals(
                entry.ErrorSignature,
                errorSignature,
                StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildContext(IReadOnlyList<ExecutionHistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return "No commands executed yet.";
        }

        var builder = new StringBuilder();
        foreach (var entry in history)
        {
            builder.AppendLine($"Timestamp: {entry.Timestamp:O}");
            builder.AppendLine($"Command: {entry.Command}");
            builder.AppendLine($"WorkingDirectory: {entry.WorkingDirectory}");
            builder.AppendLine($"ExitCode: {entry.ExitCode}");
            builder.AppendLine($"Success: {entry.Success}");
            builder.AppendLine($"Stdout: {Text(entry.StandardOutput)}");
            builder.AppendLine($"Stderr: {Text(entry.StandardError)}");
            builder.AppendLine();
        }

        var context = builder.ToString().Trim();
        return context.Length <= 16000 ? context : context[^16000..];
    }

    public static string CreateErrorSignature(
        string standardOutput,
        string standardError,
        int exitCode)
    {
        var error = string.IsNullOrWhiteSpace(standardError)
            ? standardOutput
            : standardError;
        var normalized = error.Trim().ToLowerInvariant();

        if (ContainsAny(normalized, "permission denied", "access is denied", "unauthorized"))
        {
            return "permission-denied";
        }

        if (ContainsAny(
                normalized,
                "command not found",
                "is not recognized as an internal or external command",
                "is not recognized as the name of a cmdlet",
                "was not found",
                "no such file or directory"))
        {
            return "command-not-found";
        }

        normalized = Regex.Replace(normalized, @"[a-f0-9]{8,}", "<id>");
        normalized = Regex.Replace(normalized, @"\d+", "<n>");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return $"{exitCode}:{Truncate(normalized, 300)}";
    }

    private static bool ContainsAny(string value, params string[] signals)
    {
        return signals.Any(signal => value.Contains(signal, StringComparison.Ordinal));
    }

    private static string Text(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : Truncate(value.Trim(), 2000);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

public sealed class CommandDeduplication
{
    public CommandDeduplicationResult Evaluate(
        string command,
        string workingDirectory,
        ExecutionEnvironmentSnapshot currentEnvironment,
        string? retryJustification,
        ExecutionHistory history)
    {
        if (!string.IsNullOrWhiteSpace(retryJustification))
        {
            return CommandDeduplicationResult.Allow(
                $"Explicit retry justification: {retryJustification.Trim()}");
        }

        var normalizedCommand = NormalizeCommand(command);
        var samePathEntries = history.Entries
            .Where(entry =>
                SamePath(entry.WorkingDirectory, workingDirectory) &&
                NormalizeCommand(entry.Command) == normalizedCommand)
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();

        var mostRecent = samePathEntries.FirstOrDefault();
        if (mostRecent is null)
        {
            return CommandDeduplicationResult.Allow();
        }

        if (mostRecent.Success)
        {
            if (string.Equals(
                    mostRecent.FileFingerprint,
                    currentEnvironment.FileFingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(
                    mostRecent.EnvironmentFingerprint,
                    currentEnvironment.EnvironmentFingerprint,
                    StringComparison.Ordinal))
            {
                return CommandDeduplicationResult.Block(
                    "The same command was already executed successfully and the workspace " +
                    "state has not changed since. Repeating it cannot produce new evidence. " +
                    "Provide a different command or update the workspace first.");
            }

            return CommandDeduplicationResult.Allow(
                "The workspace changed since the last successful execution of this command.");
        }

        if (!string.Equals(
                mostRecent.FileFingerprint,
                currentEnvironment.FileFingerprint,
                StringComparison.Ordinal))
        {
            return CommandDeduplicationResult.Allow(
                "Files in the working directory changed since the failed execution.");
        }

        if (!string.Equals(
                mostRecent.EnvironmentFingerprint,
                currentEnvironment.EnvironmentFingerprint,
                StringComparison.Ordinal))
        {
            return CommandDeduplicationResult.Allow(
                "Environment variables changed since the failed execution.");
        }

        return CommandDeduplicationResult.Block(
            "The same command already failed recently and neither the directory, files, " +
            "environment variables nor arguments changed. Provide a different command.");
    }

    internal static string NormalizeCommand(string command)
    {
        return Regex.Replace(command.Trim(), @"\s+", " ").ToLowerInvariant();
    }

    private static bool SamePath(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            comparison);
    }
}

public sealed record CommandDeduplicationResult(bool Allowed, string Reason)
{
    public static CommandDeduplicationResult Allow(string reason = "")
    {
        return new CommandDeduplicationResult(true, reason);
    }

    public static CommandDeduplicationResult Block(string reason)
    {
        return new CommandDeduplicationResult(false, reason);
    }
}

public sealed record ExecutionEnvironmentSnapshot(
    string EnvironmentFingerprint,
    string FileFingerprint)
{
    public static ExecutionEnvironmentSnapshot Capture(string workingDirectory)
    {
        return new ExecutionEnvironmentSnapshot(
            HashEnvironment(),
            HashFiles(workingDirectory));
    }

    private static string HashEnvironment()
    {
        var values = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .OrderBy(entry => entry.Key?.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Key}={entry.Value}");
        return Hash(string.Join('\n', values));
    }

    private static string HashFiles(string workingDirectory)
    {
        try
        {
            var files = Directory
                .EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !IsIgnored(path))
                .Take(2048)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return $"{Path.GetRelativePath(workingDirectory, path)}|" +
                           $"{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                })
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
            return Hash(string.Join('\n', files));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Hash($"unavailable:{ex.GetType().Name}");
        }
    }

    private static bool IsIgnored(string path)
    {
        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
