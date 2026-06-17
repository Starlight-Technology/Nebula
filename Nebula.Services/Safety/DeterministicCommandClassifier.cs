using System.Text.RegularExpressions;

using Nebula.Core.Safety;

namespace Nebula.Services.Safety;

public sealed partial class DeterministicCommandClassifier : ICommandClassifier
{
    private static readonly string[] SensitiveTerms =
    [
        ".env", ".ssh", "id_rsa", "id_ed25519", "credentials", "credential",
        "api_key", "apikey", "access_token", "auth_token", "private key", "secret key"
    ];

    private readonly string workspaceRoot;
    private readonly string controlledTempRoot;
    private readonly IScriptContentSafetyClassifier scriptContentClassifier;

    public DeterministicCommandClassifier(
        string? workspaceRoot = null,
        IScriptContentSafetyClassifier? scriptContentClassifier = null)
    {
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? Environment.CurrentDirectory);
        controlledTempRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "Nebula"));
        this.scriptContentClassifier =
            scriptContentClassifier ??
            new ScriptContentSafetyClassifier(
                new FileWriteSafetyClassifier(this.workspaceRoot));
    }

    public Task<CommandClassification> ClassifyAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedCommand = commandText?.Trim() ?? string.Empty;
        var command = ExtractShellPayload(resolvedCommand);
        var normalized = command.ToLowerInvariant();

        var classification = ClassifyBlocked(command, normalized)
            ?? ClassifyApprovalRequired(command, normalized)
            ?? ClassifyAllowed(command, normalized)
            ?? Result(command, CommandIntent.Unknown, 0.20, "No deterministic rule matched.");

        return Task.FromResult(classification);
    }

    private static string ExtractShellPayload(string command)
    {
        var powerShell = PowerShellWrapperRegex().Match(command);
        if (powerShell.Success)
        {
            return UnescapeShellPayload(powerShell.Groups["command"].Value);
        }

        var cmd = CmdWrapperRegex().Match(command);
        if (cmd.Success)
        {
            return cmd.Groups["command"].Value.Trim();
        }

        var unix = UnixWrapperRegex().Match(command);
        return unix.Success
            ? UnescapeShellPayload(unix.Groups["command"].Value)
            : command;
    }

    private static string UnescapeShellPayload(string command) =>
        command.Replace("\\\"", "\"", StringComparison.Ordinal).Trim();

    private static CommandClassification? ClassifyBlocked(string command, string normalized)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Result(command, CommandIntent.Blocked, 1.00, "Empty commands cannot be executed.");
        }

        if (PolicyBypassRegex().IsMatch(normalized))
        {
            return Result(command, CommandIntent.Blocked, 1.00, "The command attempts to bypass or disable the safety policy.");
        }

        if (RemoteScriptExecutionRegex().IsMatch(normalized))
        {
            return Result(command, CommandIntent.Blocked, 1.00, "The command downloads remote content and executes it directly.");
        }

        if (CatastrophicDeleteRegex().IsMatch(normalized) || DiskFormatRegex().IsMatch(normalized))
        {
            return Result(command, CommandIntent.Blocked, 1.00, "The command can destroy a system, home directory, or disk.");
        }

        if (SensitiveTerms.Any(normalized.Contains))
        {
            var reason = HasTransferOrArchiveOperation(normalized)
                ? "The command reads, archives, or transfers credentials or other sensitive data."
                : "Access to credential, token, .env, or SSH material is not allowed automatically.";
            return Result(command, CommandIntent.DataExfiltration, 0.99, reason);
        }

        if (ArchiveAndSendRegex().IsMatch(normalized))
        {
            return Result(command, CommandIntent.DataExfiltration, 0.98, "The command combines user-data collection or archiving with transfer.");
        }

        return null;
    }

    private CommandClassification? ClassifyApprovalRequired(string command, string normalized)
    {
        if (PackageInstallRegex().IsMatch(normalized))
        {
            return Result(command, CommandIntent.PackageInstall, 0.99, "Package installation changes the local dependency set and may run third-party code.");
        }

        if (NetworkRegex().IsMatch(normalized))
        {
            return Result(command, CommandIntent.NetworkAccess, 0.98, "The command accesses an external network resource.");
        }

        if (PrivilegedRegex().IsMatch(normalized))
        {
            return Result(command, CommandIntent.PrivilegedOperation, 0.99, "The command requests elevated execution or changes ownership/permissions.");
        }

        if (PersistentProcessRegex().IsMatch(normalized))
        {
            return Result(command, CommandIntent.NeedsApproval, 0.96, "The command creates a persistent, detached, scheduled, or startup process.");
        }

        if (GlobalEnvironmentRegex().IsMatch(normalized))
        {
            return Result(command, CommandIntent.NeedsApproval, 0.95, "The command changes global environment or PATH configuration.");
        }

        if (TryGetDirectoryCreationTarget(command, out var directoryTarget) &&
            !IsInsideWorkspace(directoryTarget))
        {
            return Result(command, CommandIntent.NeedsApproval, 0.98, $"The directory target is outside the workspace: {directoryTarget}");
        }

        if (TryGetWriteTarget(command, out var target) && !IsInsideWorkspace(target))
        {
            return Result(command, CommandIntent.NeedsApproval, 0.98, $"The write target is outside the workspace: {target}");
        }

        if (GeneralDestructiveRegex().IsMatch(normalized) && !IsWorkspaceBuildCleanup(normalized))
        {
            return Result(command, CommandIntent.DestructiveOperation, 0.94, "The command performs recursive or broad deletion.");
        }

        if (UnknownExecutableRegex().IsMatch(command))
        {
            return Result(command, CommandIntent.NeedsApproval, 0.72, "Execution of an unknown local binary requires approval.");
        }

        if (TryGetPythonScriptPath(command, out _) && !IsSimplePythonScript(command))
        {
            return Result(command, CommandIntent.NeedsApproval, 0.90, "Only inspectable Python scripts with simple local operations are allowed automatically.");
        }

        return null;
    }

    private CommandClassification? ClassifyAllowed(string command, string normalized)
    {
        if (SimpleOutputRegex().IsMatch(command))
        {
            return Result(command, CommandIntent.SafeExecuteLocal, 0.99, "The command contains only simple local output or arithmetic.");
        }

        if (SafeReadOnlyRegex().IsMatch(command))
        {
            return Result(command, CommandIntent.SafeReadOnly, 0.99, "The command is on the read-only allowlist.");
        }

        if (LocalFileReadRegex().IsMatch(command) && !SensitiveTerms.Any(normalized.Contains))
        {
            return Result(command, CommandIntent.SafeReadOnly, 0.97, "The command reads a non-sensitive local file.");
        }

        if (DotnetSafeRegex().IsMatch(command))
        {
            return Result(command, CommandIntent.SafeExecuteLocal, 0.99, "dotnet build and dotnet test are explicitly allowed.");
        }

        if (PythonInlinePrintRegex().IsMatch(command) || IsSimplePythonScript(command))
        {
            return Result(command, CommandIntent.SafeExecuteLocal, 0.96, "The Python command is limited to an inspected local script or a simple print expression.");
        }

        if (TryGetDirectoryCreationTarget(command, out var directoryTarget) &&
            IsInsideWorkspace(directoryTarget))
        {
            return Result(command, CommandIntent.SafeWriteLocal, 0.97, "The command creates a directory inside the workspace or controlled temp root.");
        }

        if (TryGetWriteTarget(command, out var target)
            && IsAllowedWorkspaceFile(target)
            && SafeFileWriteRegex().IsMatch(command))
        {
            return Result(command, CommandIntent.SafeWriteLocal, 0.97, "The command creates an allowed text or source file inside the workspace.");
        }

        if (IsWorkspaceBuildCleanup(normalized))
        {
            return Result(command, CommandIntent.SafeWriteLocal, 0.93, "The command only removes generated bin/obj artifacts in the workspace.");
        }

        return null;
    }

    private bool IsSimplePythonScript(string command)
    {
        if (!TryGetPythonScriptPath(command, out var scriptPath))
        {
            return false;
        }

        var fullPath = Path.IsPathRooted(scriptPath)
            ? Path.GetFullPath(scriptPath)
            : Path.GetFullPath(scriptPath, workspaceRoot);
        if (!IsInsideWorkspace(fullPath) || !File.Exists(fullPath))
        {
            return false;
        }

        var content = File.ReadAllText(fullPath);
        var classification = scriptContentClassifier.Classify(
            content,
            "python",
            fullPath);
        return classification.Intent is
            CommandIntent.SafeReadOnly or
            CommandIntent.SafeWriteLocal or
            CommandIntent.SafeExecuteLocal &&
            classification.Confidence >= 0.95;
    }

    private static bool TryGetPythonScriptPath(string command, out string scriptPath)
    {
        var match = PythonScriptRegex().Match(command);
        scriptPath = match.Success ? CleanPath(match.Groups[1].Value) : string.Empty;
        return match.Success;
    }

    private bool IsAllowedWorkspaceFile(string target)
    {
        var extension = Path.GetExtension(target);
        return IsInsideWorkspace(target)
            && new[] { ".txt", ".md", ".json", ".cs", ".py" }
                .Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private bool IsInsideWorkspace(string path)
    {
        if (WindowsAbsolutePathRegex().IsMatch(path))
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            return IsUnder(fullPath, workspaceRoot) ||
                   IsUnder(fullPath, controlledTempRoot);
        }

        var candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, workspaceRoot);
        return IsUnder(candidate, workspaceRoot) ||
               IsUnder(candidate, controlledTempRoot);
    }

    private static bool IsUnder(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool TryGetWriteTarget(string command, out string target)
    {
        var redirect = RedirectTargetRegex().Match(command);
        if (redirect.Success)
        {
            target = CleanPath(redirect.Groups[1].Value);
            return true;
        }

        var creation = FileCreationRegex().Match(command);
        if (creation.Success)
        {
            target = CleanPath(creation.Groups[1].Value);
            return true;
        }

        var setContent = SetContentTargetRegex().Match(command);
        if (setContent.Success)
        {
            target = CleanPath(setContent.Groups["path"].Value);
            return true;
        }

        target = string.Empty;
        return false;
    }

    private static bool TryGetDirectoryCreationTarget(
        string command,
        out string target)
    {
        var match = DirectoryCreationRegex().Match(command);
        target = match.Success ? CleanPath(match.Groups["path"].Value) : string.Empty;
        return match.Success;
    }

    private static string CleanPath(string path) => path.Trim().Trim('"', '\'');

    private static bool HasTransferOrArchiveOperation(string command) =>
        NetworkRegex().IsMatch(command) || ArchiveRegex().IsMatch(command) || LocalFileReadRegex().IsMatch(command);

    private static bool IsWorkspaceBuildCleanup(string command) =>
        WorkspaceBuildCleanupRegex().IsMatch(command);

    private static CommandClassification Result(
        string command,
        CommandIntent intent,
        double confidence,
        string reason) =>
        new(command, intent, confidence, nameof(DeterministicCommandClassifier), [reason]);

    [GeneratedRegex(@"(?:ignore|bypass|disable|evade|circumvent|burle|ignore|desative).{0,30}(?:policy|safety|seguran[cç]a|prote[cç][aã]o)|(?:sem|without).{0,15}(?:approval|confirma[cç][aã]o)", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyBypassRegex();

    [GeneratedRegex(@"(?:curl|wget|invoke-webrequest|iwr|invoke-restmethod)[^\r\n|;&]*https?://[^\r\n|;&]*\|\s*(?:sh|bash|zsh|fish|powershell|pwsh|iex|invoke-expression)\b|https?://[^\r\n|;&]*\|\s*(?:sh|bash|zsh|fish|powershell|pwsh|iex|invoke-expression)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteScriptExecutionRegex();

    [GeneratedRegex(@"(?:rm\s+-[^\r\n]*r[^\r\n]*f[^\r\n]*(?:/\s*$|~|\$home|/home)|del\s+/s\s+/q\s+[a-z]:\\(?:users?|documents and settings|windows|system32)?(?:\\|\s|$)|remove-item[^\r\n]*-recurse[^\r\n]*-force[^\r\n]*[a-z]:\\(?:users?|documents and settings|windows|system32)?(?:\\|\s|$)|(?:rm|del|remove-item)[^\r\n]*(?:/etc|/usr|/var|windows\\system32|home inteiro|entire home))", RegexOptions.IgnoreCase)]
    private static partial Regex CatastrophicDeleteRegex();

    [GeneratedRegex(@"(?:^|\s)(?:format(?:\.com)?\s+[a-z]:|mkfs(?:\.|\s)|diskpart\b|dd\s+if=.*\s+of=/dev/)", RegexOptions.IgnoreCase)]
    private static partial Regex DiskFormatRegex();

    [GeneratedRegex(@"(?:tar|zip|7z|compress|compact).*(?:curl|wget|scp|ftp|http)|(?:curl|wget|scp|ftp).*(?:tar|zip|7z|home|users?)", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveAndSendRegex();

    [GeneratedRegex(@"^(?:pip(?:3)?\s+install|python\s+-m\s+pip\s+install|npm\s+(?:i|install)|yarn\s+add|pnpm\s+(?:add|install)|dotnet\s+add\s+.+\s+package|apt(?:-get)?\s+install|brew\s+install)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PackageInstallRegex();

    [GeneratedRegex(@"(?:^|\s|[;&|])(?:curl|wget|Invoke-WebRequest|iwr|Invoke-RestMethod|scp|ftp|ssh)\b|https?://|acesso\s+(?:a|à)\s+internet|access\s+the\s+internet", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkRegex();

    [GeneratedRegex(@"(?:^|\s|[;&|])sudo\b|(?:^|\s|[;&|])(?:chmod|chown)\b|runas\b|start-process[^\r\n]*-verb\s+runas", RegexOptions.IgnoreCase)]
    private static partial Regex PrivilegedRegex();

    [GeneratedRegex(@"\b(?:nohup|disown|systemctl\s+enable|schtasks|crontab|new-service|sc\.exe\s+create)\b|(?:^|\s)&\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PersistentProcessRegex();

    [GeneratedRegex(@"setx\b|\[environment\]::setenvironmentvariable|/etc/(?:environment|profile)|(?:machine|global).{0,20}(?:path|environment|env)", RegexOptions.IgnoreCase)]
    private static partial Regex GlobalEnvironmentRegex();

    [GeneratedRegex(@"(?:rm\s+-[^\r\n]*r|del\s+/s|remove-item[^\r\n]*-recurse|rmdir\s+/s)", RegexOptions.IgnoreCase)]
    private static partial Regex GeneralDestructiveRegex();

    [GeneratedRegex(@"^(?:\.\\|\./)[^\s]+(?:\.exe|\.bin|\.run)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UnknownExecutableRegex();

    [GeneratedRegex(@"^(?:echo(?:\s+[^;&|`<>]+)?|dir(?:\s+[^&|;<>`]+)?|ls(?:\s+(?:-[a-z]+\s*)?[^&|;<>`]*)?|pwd|get-location|get-childitem(?:\s+-(?:literal)?path\s+[^&|;<>`]+)?|select-string\s+-pattern\s+[^&|;<>`]+\s+-(?:literal)?path\s+[^&|;<>`]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeReadOnlyRegex();

    [GeneratedRegex(@"^(?:cat|type|get-content(?:\s+-(?:literal)?path)?)\s+(?!.*(?:https?://))[^;&|<>`]+$", RegexOptions.IgnoreCase)]
    private static partial Regex LocalFileReadRegex();

    [GeneratedRegex(@"^dotnet\s+(?:build|test)(?:\s+[^;&|]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DotnetSafeRegex();

    [GeneratedRegex(@"^(?:python|python3|py)\s+-c\s+['""]\s*print\s*\([^;&|]*\)\s*['""]$", RegexOptions.IgnoreCase)]
    private static partial Regex PythonInlinePrintRegex();

    [GeneratedRegex(
        @"^(?:python|python3|py)\s+[""']?([^""';&|]+?\.py)[""']?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PythonScriptRegex();

    [GeneratedRegex(@"^\s*(?:print\s*\([^;&|]*\)|console\.writeline\s*\([^;&|]*\)|[-+*/().\d\s]+)\s*;?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SimpleOutputRegex();


    [GeneratedRegex(@"(?:>>|>|out-file\s+)(?:\s*)([^;&|>]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RedirectTargetRegex();

    [GeneratedRegex(@"^(?:(?:echo|printf)\s+[^;&|`$]+\s*(?:>>|>)\s*[^;&|]+|(?:touch|new-item)\s+(?:-[a-z]+\s+)*(?:-path\s+)?[^;&|]+|set-content\s+(?:-path\s+)?[^;&|]+\s+-value\s+[^;&|`]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeFileWriteRegex();

    [GeneratedRegex(@"^(?:touch|new-item)\s+(?:-[a-z]+\s+)*(?:-path\s+)?([^;&|]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileCreationRegex();

    [GeneratedRegex(@"^set-content\s+(?:-path\s+)?(?<path>[^;&|]+?)\s+-value\s+[^;&|`]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SetContentTargetRegex();

    [GeneratedRegex(@"^(?:mkdir\s+(?:-p\s+)?(?<path>[^;&|]+)|new-item\s+(?=.*(?:-itemtype\s+directory|directory))(?=.*-path\s+).*?-path\s+(?<path>[^;&|]+))$", RegexOptions.IgnoreCase)]
    private static partial Regex DirectoryCreationRegex();

    [GeneratedRegex(@"^(?:(?:rm\s+-rf|rmdir\s+/s\s+/q|remove-item\s+-recurse\s+-force)\s+)?(?:(?:\./)?(?:bin|obj)(?:\s+(?:\./)?(?:bin|obj))?|(?:find|dotnet\s+clean).*(?:bin|obj))$", RegexOptions.IgnoreCase)]
    private static partial Regex WorkspaceBuildCleanupRegex();

    [GeneratedRegex(@"^(?:[a-z]:[\\/]|\\\\)", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsAbsolutePathRegex();

    [GeneratedRegex(@"(?:tar|zip|7z|compress|compact)", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveRegex();

    [GeneratedRegex(
        @"^(?:powershell|pwsh)(?:\.exe)?\s+.*?-(?:command|c)\s+""(?<command>.*)""\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellWrapperRegex();

    [GeneratedRegex(
        @"^cmd(?:\.exe)?\s+/[ck]\s+(?<command>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CmdWrapperRegex();

    [GeneratedRegex(
        @"^(?:/bin/)?(?:bash|sh)\s+-c\s+""(?<command>.*)""\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnixWrapperRegex();
}
