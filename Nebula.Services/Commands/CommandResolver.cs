using System.Text.RegularExpressions;

using Nebula.Core.Commands;

namespace Nebula.Services.Commands;

public sealed partial class CommandResolver : ICommandResolver
{
    public ResolvedCommand Resolve(
        CommandRequest request,
        RuntimeCommandEnvironment environment)
    {
        var rawCommand = request.RawCommand?.Trim() ?? string.Empty;
        var intentText = $"{request.UserText} {rawCommand}".Trim();

        if (IsDeleteIntent(intentText))
        {
            return ResolveRawCommand(
                rawCommand,
                environment,
                "Destructive commands are preserved for policy review and are never translated automatically.");
        }

        if (IsCurrentDirectoryIntent(intentText))
        {
            return ResolveCurrentDirectory(environment);
        }

        if (IsListDirectoryIntent(intentText))
        {
            var path = ResolveTargetPath(request, environment);
            return ResolveListDirectory(path, environment);
        }

        if (IsCreateDirectoryIntent(intentText))
        {
            var path = ResolveTargetPath(request, environment);
            return ResolveCreateDirectory(path, environment);
        }

        if (environment.OS == OperatingSystemKind.Windows &&
            TryResolveWindowsUnixCommand(rawCommand, environment, out var compatible))
        {
            return compatible;
        }

        return ResolveRawCommand(
            rawCommand,
            environment,
            "No catalog mapping matched; the command will use the detected shell and still pass through policy.");
    }

    private static ResolvedCommand ResolveListDirectory(
        string path,
        RuntimeCommandEnvironment environment)
    {
        return environment switch
        {
            { OS: OperatingSystemKind.Windows, Shell: ShellKind.PowerShell } =>
                PowerShell(
                    $"Get-ChildItem -LiteralPath {QuotePowerShell(path)}",
                    environment,
                    "Mapped directory listing to PowerShell Get-ChildItem."),
            { OS: OperatingSystemKind.Windows, Shell: ShellKind.Cmd } =>
                Cmd(
                    $"dir {QuoteCmd(path)}",
                    environment,
                    "Mapped directory listing to CMD dir."),
            { OS: OperatingSystemKind.Linux or OperatingSystemKind.MacOS } =>
                Unix(
                    $"ls -la {QuoteUnix(path)}",
                    environment,
                    "Mapped directory listing to Unix ls."),
            _ => ResolveRawCommand(
                $"ls -la {QuoteUnix(path)}",
                environment,
                "The runtime environment is unknown; policy approval is required before execution.")
        };
    }

    private static ResolvedCommand ResolveCurrentDirectory(
        RuntimeCommandEnvironment environment)
    {
        return environment switch
        {
            { OS: OperatingSystemKind.Windows, Shell: ShellKind.PowerShell } =>
                PowerShell(
                    "Get-Location",
                    environment,
                    "Mapped current-directory lookup to PowerShell Get-Location."),
            { OS: OperatingSystemKind.Windows, Shell: ShellKind.Cmd } =>
                Cmd(
                    "cd",
                    environment,
                    "Mapped current-directory lookup to CMD cd."),
            { OS: OperatingSystemKind.Linux or OperatingSystemKind.MacOS } =>
                Unix(
                    "pwd",
                    environment,
                    "Mapped current-directory lookup to Unix pwd."),
            _ => ResolveRawCommand(
                "pwd",
                environment,
                "The runtime environment is unknown; policy approval is required before execution.")
        };
    }

    private static ResolvedCommand ResolveCreateDirectory(
        string path,
        RuntimeCommandEnvironment environment)
    {
        return environment switch
        {
            { OS: OperatingSystemKind.Windows, Shell: ShellKind.PowerShell } =>
                PowerShell(
                    $"New-Item -ItemType Directory -Path {QuotePowerShell(path)}",
                    environment,
                    "Mapped directory creation to PowerShell New-Item."),
            { OS: OperatingSystemKind.Windows, Shell: ShellKind.Cmd } =>
                Cmd(
                    $"mkdir {QuoteCmd(path)}",
                    environment,
                    "Mapped directory creation to CMD mkdir."),
            { OS: OperatingSystemKind.Linux or OperatingSystemKind.MacOS } =>
                Unix(
                    $"mkdir -p {QuoteUnix(path)}",
                    environment,
                    "Mapped directory creation to Unix mkdir -p."),
            _ => ResolveRawCommand(
                $"mkdir {QuoteUnix(path)}",
                environment,
                "The runtime environment is unknown; policy approval is required before execution.")
        };
    }

    private static bool TryResolveWindowsUnixCommand(
        string rawCommand,
        RuntimeCommandEnvironment environment,
        out ResolvedCommand resolved)
    {
        var commandName = FirstTokenRegex().Match(rawCommand).Groups["command"].Value;
        if (commandName.Equals("cat", StringComparison.OrdinalIgnoreCase))
        {
            var catPath = rawCommand[commandName.Length..].Trim();
            resolved = PowerShell(
                $"Get-Content -LiteralPath {QuotePowerShell(Unquote(catPath))}",
                environment,
                "Converted Unix cat to the safe PowerShell Get-Content equivalent.");
            return true;
        }

        if (commandName.Equals("grep", StringComparison.OrdinalIgnoreCase) &&
            TryParseSimpleGrep(rawCommand, out var pattern, out var path))
        {
            resolved = PowerShell(
                $"Select-String -Pattern {QuotePowerShell(pattern)} -LiteralPath {QuotePowerShell(path)}",
                environment,
                "Converted a simple Unix grep command to PowerShell Select-String.");
            return true;
        }

        if (commandName is not "" &&
            new[] { "rm", "chmod", "chown", "grep" }
                .Contains(commandName, StringComparer.OrdinalIgnoreCase))
        {
            resolved = ResolveRawCommand(
                rawCommand,
                environment,
                $"Unix command '{commandName}' has no automatic safe Windows translation and requires policy review.");
            return true;
        }

        resolved = default!;
        return false;
    }

    private static ResolvedCommand ResolveRawCommand(
        string rawCommand,
        RuntimeCommandEnvironment environment,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return new ResolvedCommand(
                string.Empty,
                string.Empty,
                string.Empty,
                environment.WorkingDirectory,
                [reason, "No command text was available to resolve."]);
        }

        return environment.Shell switch
        {
            ShellKind.PowerShell => PowerShell(
                rawCommand,
                environment,
                reason,
                displayShellInvocation: false),
            ShellKind.Cmd => Cmd(
                rawCommand,
                environment,
                reason,
                displayShellInvocation: false),
            ShellKind.Bash or ShellKind.Sh => Unix(
                rawCommand,
                environment,
                reason,
                displayShellInvocation: false),
            _ => new ResolvedCommand(
                string.Empty,
                string.Empty,
                rawCommand,
                environment.WorkingDirectory,
                [reason, "No compatible shell was detected."])
        };
    }

    private static ResolvedCommand PowerShell(
        string command,
        RuntimeCommandEnvironment environment,
        string reason,
        bool displayShellInvocation = true)
    {
        var fileName = GetPowerShellExecutable();
        var arguments =
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{EscapeDoubleQuotedArgument(command)}\"";
        return new ResolvedCommand(
            fileName,
            arguments,
            displayShellInvocation ? $"{fileName} {arguments}" : command,
            environment.WorkingDirectory,
            [reason, "Selected PowerShell for Windows."]);
    }

    private static ResolvedCommand Cmd(
        string command,
        RuntimeCommandEnvironment environment,
        string reason,
        bool displayShellInvocation = true)
    {
        const string fileName = "cmd.exe";
        var arguments = $"/c {command}";
        return new ResolvedCommand(
            fileName,
            arguments,
            displayShellInvocation ? $"{fileName} {arguments}" : command,
            environment.WorkingDirectory,
            [reason, "Selected CMD for Windows."]);
    }

    private static ResolvedCommand Unix(
        string command,
        RuntimeCommandEnvironment environment,
        string reason,
        bool displayShellInvocation = true)
    {
        var fileName = environment.Shell == ShellKind.Bash ? "/bin/bash" : "/bin/sh";
        var arguments = $"-c \"{EscapeDoubleQuotedArgument(command)}\"";
        return new ResolvedCommand(
            fileName,
            arguments,
            displayShellInvocation ? $"{fileName} {arguments}" : command,
            environment.WorkingDirectory,
            [reason, $"Selected {environment.Shell} for a Unix-like OS."]);
    }

    private static string ResolveTargetPath(
        CommandRequest request,
        RuntimeCommandEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(request.RequestedDrive))
        {
            return $"{request.RequestedDrive.Trim().TrimEnd(':').ToUpperInvariant()}:\\";
        }

        return string.IsNullOrWhiteSpace(request.RequestedPath)
            ? environment.WorkingDirectory
            : request.RequestedPath.Trim();
    }

    private static bool IsListDirectoryIntent(string text) =>
        ListDirectoryRegex().IsMatch(text);

    private static bool IsCurrentDirectoryIntent(string text) =>
        CurrentDirectoryRegex().IsMatch(text);

    private static bool IsCreateDirectoryIntent(string text) =>
        CreateDirectoryRegex().IsMatch(text);

    private static bool IsDeleteIntent(string text) =>
        DeleteRegex().IsMatch(text);

    private static bool TryParseSimpleGrep(
        string command,
        out string pattern,
        out string path)
    {
        var match = SimpleGrepRegex().Match(command);
        pattern = match.Success ? Unquote(match.Groups["pattern"].Value) : string.Empty;
        path = match.Success ? Unquote(match.Groups["path"].Value) : string.Empty;
        return match.Success;
    }

    private static string QuotePowerShell(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuoteCmd(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteUnix(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string EscapeDoubleQuotedArgument(string value) =>
        value.Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Unquote(string value) => value.Trim().Trim('"', '\'');

    private static string GetPowerShellExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "powershell.exe";
        }

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

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path) &&
            path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory.Trim(), "pwsh.exe"))
                .Any(File.Exists))
        {
            return "pwsh.exe";
        }

        return "powershell.exe";
    }

    [GeneratedRegex(
        @"(?:^|\s)(?:ls|dir|get-childitem)(?:\s|$)|\b(?:listar|liste|mostre|mostrar|exibir|exiba|list)\b.{0,40}\b(?:arquivos|files|diret[oó]rio|directory|unidade|drive)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ListDirectoryRegex();

    [GeneratedRegex(
        @"(?:^|\s)(?:pwd|get-location)(?:\s|$)|\b(?:diret[oó]rio|pasta|directory)\s+(?:atual|corrente|current)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex CurrentDirectoryRegex();

    [GeneratedRegex(
        @"(?:^|\s)(?:mkdir|md)(?:\s|$)|\b(?:criar|crie|create)\b.{0,20}\b(?:pasta|diret[oó]rio|folder|directory)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreateDirectoryRegex();

    [GeneratedRegex(
        @"(?:^|\s)(?:rm|del|erase|rmdir|remove-item)(?:\s|$)|\b(?:apagar|apague|deletar|delete|remover|remove|excluir)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeleteRegex();

    [GeneratedRegex(@"^\s*(?<command>[^\s]+)")]
    private static partial Regex FirstTokenRegex();

    [GeneratedRegex(
        @"^\s*grep\s+(?<pattern>""[^""]+""|'[^']+'|[^\s]+)\s+(?<path>""[^""]+""|'[^']+'|[^\s]+)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SimpleGrepRegex();
}
