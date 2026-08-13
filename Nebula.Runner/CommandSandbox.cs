using System.Text;
using System.Text.RegularExpressions;

using Nebula.Core.Commands;
using Nebula.Core.Configuration;
using Nebula.Core.Execution;

namespace Nebula.Runner;

public interface ICommandSandbox
{
    SandboxMode Mode { get; }

    bool IsEligible(ShellKind shellKind);

    Task<ShellCommandResult> RunSandboxedAsync(
        ShellKind shellKind,
        ResolvedCommand command,
        string workingDirectory,
        CancellationToken cancellationToken);
}

public sealed class DockerCommandSandbox : ICommandSandbox
{
    private readonly IResolvedCommandExecutor executor;
    private readonly NebulaRuntimeSettings settings;

    public DockerCommandSandbox(
        IResolvedCommandExecutor executor,
        NebulaRuntimeSettings settings)
    {
        this.executor = executor;
        this.settings = settings;
    }

    public SandboxMode Mode => settings.SandboxMode;

    public bool IsEligible(ShellKind shellKind) =>
        shellKind is ShellKind.PowerShell or ShellKind.Bash or ShellKind.Sh;

    public async Task<ShellCommandResult> RunSandboxedAsync(
        ShellKind shellKind,
        ResolvedCommand command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var dockerArguments = BuildDockerArguments(
            shellKind,
            command,
            workingDirectory);
        var dockerCommand = new ResolvedCommand(
            "docker",
            dockerArguments,
            $"{command.DisplayCommand} (sandbox docker)",
            workingDirectory,
            ["Executed inside an isolated Docker container."]);

        return await executor.RunCommandDetailedAsync(
            dockerCommand,
            cancellationToken);
    }

    private string BuildDockerArguments(
        ShellKind shellKind,
        ResolvedCommand command,
        string workingDirectory)
    {
        var arguments = new List<string>
        {
            "run",
            "--rm",
            "--network",
            "none",
            "--cap-drop",
            "ALL",
            "--security-opt",
            "no-new-privileges"
        };

        if (settings.SandboxMemoryLimitMb > 0)
        {
            arguments.Add("--memory");
            arguments.Add($"{settings.SandboxMemoryLimitMb}m");
        }

        if (settings.SandboxCpuLimit > 0)
        {
            arguments.Add("--cpus");
            arguments.Add(settings.SandboxCpuLimit.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        var workspaceMount = NormalizeWorkspacePath(workingDirectory);
        arguments.Add("-v");
        arguments.Add($"\"{workspaceMount}:/workspace:rw\"");
        arguments.Add("-w");
        arguments.Add("/workspace");
        arguments.Add(settings.SandboxImage);

        arguments.AddRange(BuildShellInvocation(shellKind, command, workingDirectory));

        return string.Join(' ', arguments);
    }

    private static IEnumerable<string> BuildShellInvocation(
        ShellKind shellKind,
        ResolvedCommand command,
        string workingDirectory)
    {
        var body = ExtractShellBody(shellKind, command);
        body = TranslateWorkspacePaths(body, workingDirectory);
        switch (shellKind)
        {
            case ShellKind.Bash:
            case ShellKind.Sh:
                return
                [
                    "bash",
                    "-c",
                    $"\"{EscapeForBash(body)}\""
                ];
            default:
                return
                [
                    "pwsh",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"\"{EscapeForPowerShell(body)}\""
                ];
        }
    }

    private static string TranslateWorkspacePaths(
        string body,
        string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(body) ||
            string.IsNullOrWhiteSpace(workingDirectory))
        {
            return body;
        }

        var normalized =
            System.IO.Path.GetFullPath(workingDirectory).TrimEnd('\\', '/');
        var candidates = new[]
        {
            normalized,
            normalized.Replace('\\', '/'),
            normalized.Replace('/', '\\')
        };

        var translated = body;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            translated = Regex.Replace(
                translated,
                $"(?<![\\w]){Regex.Escape(candidate)}(?=[\\\\/\"'\\s]|$)",
                "/workspace",
                RegexOptions.IgnoreCase);
        }

        return translated;
    }

    private static string ExtractShellBody(
        ShellKind shellKind,
        ResolvedCommand command)
    {
        var flag = shellKind == ShellKind.PowerShell ? "-Command" : "-c";
        var candidate = string.IsNullOrWhiteSpace(command.Arguments)
            ? command.DisplayCommand
            : command.Arguments;

        var body = TryUnwrapFlag(candidate, flag) ?? candidate;
        for (var depth = 0; depth < 3; depth++)
        {
            if (!StartsWithShellExecutable(body))
            {
                break;
            }

            var next = TryUnwrapFlag(body, flag);
            if (next is null)
            {
                break;
            }

            body = next;
        }

        return body;
    }

    private static string? TryUnwrapFlag(string text, string flag)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var flagIndex = FindFlagIndex(text, flag);
        if (flagIndex < 0)
        {
            return null;
        }

        var payload = text[(flagIndex + flag.Length)..].TrimStart();
        if (payload.Length == 0)
        {
            return null;
        }

        if (!payload.StartsWith('"'))
        {
            return payload.Trim().Trim('"', '\'');
        }

        var builder = new StringBuilder();
        var index = 1;
        while (index < payload.Length)
        {
            var current = payload[index];
            if (current == '\\' && index + 1 < payload.Length && payload[index + 1] == '"')
            {
                builder.Append('"');
                index += 2;
                continue;
            }

            if (current == '"')
            {
                return builder.ToString();
            }

            builder.Append(current);
            index += 1;
        }

        return builder.ToString();
    }

    private static int FindFlagIndex(string text, string flag)
    {
        var index = 0;
        while (index < text.Length)
        {
            var relative = text.IndexOf(flag, index, StringComparison.OrdinalIgnoreCase);
            if (relative < 0)
            {
                return -1;
            }

            var before = relative == 0 ? ' ' : text[relative - 1];
            var after = relative + flag.Length < text.Length
                ? text[relative + flag.Length]
                : ' ';
            if (char.IsWhiteSpace(before) && char.IsWhiteSpace(after))
            {
                return relative;
            }

            index = relative + flag.Length;
        }

        return -1;
    }

    private static bool StartsWithShellExecutable(string value)
    {
        var firstToken = value.TrimStart()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        var normalized = firstToken.Trim('"', '\'', '\\').ToLowerInvariant();
        return normalized is "powershell" or "powershell.exe" or "pwsh" or "pwsh.exe"
            or "bash" or "sh" or "cmd" or "cmd.exe";
    }

    private static string NormalizeWorkspacePath(string workingDirectory)
    {
        var path = System.IO.Path.GetFullPath(workingDirectory);
        return path.TrimEnd('\\', '/');
    }

    private static string EscapeForPowerShell(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeForBash(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
}
