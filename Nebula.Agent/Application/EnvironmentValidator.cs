using Nebula.Core.Commands;
using Nebula.Core.Operations;
using Nebula.Runner;

namespace Nebula.Agent.Application;

public sealed class EnvironmentValidator(
    IShellExecutor executor,
    ICommandIntentParser commandIntentParser,
    ICommandResolver commandResolver,
    IRuntimeCommandEnvironmentDetector environmentDetector) : IEnvironmentValidator
{
    private static readonly IReadOnlyList<EnvironmentCheck> Checks = BuildChecks();

    public async Task<EnvironmentValidation> ValidateAsync(
        OperationKind operationKind,
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (operationKind is not (OperationKind.TerminalCommand or OperationKind.ScriptExecution))
        {
            return new EnvironmentValidation(true, []);
        }

        var issues = new List<EnvironmentIssue>();
        var environment = environmentDetector.Detect(workingDirectory);
        var request = commandIntentParser.Parse(command, command, workingDirectory);
        var resolved = commandResolver.Resolve(request, environment);
        var executable = resolved.FileName?.ToLowerInvariant() ?? string.Empty;

        foreach (var check in Checks)
        {
            if (check.Matches(executable, command))
            {
                var result = await check.Validate(executor, workingDirectory, cancellationToken);
                if (!result.IsValid)
                {
                    issues.AddRange(result.Issues);
                }
            }
        }

        return new EnvironmentValidation(issues.Count == 0, issues);
    }

    private static IReadOnlyList<EnvironmentCheck> BuildChecks()
    {
        return
        [
            // Python
            new EnvironmentCheck(
                "python",
                (exe, cmd) => exe.Contains("python"),
                async (exec, dir, ct) =>
                {
                    var issues = new List<EnvironmentIssue>();
                    var result = await exec.RunCommandAsync(
                        OperatingSystem.IsWindows() ? "where python" : "which python3", ct);
                    if (string.IsNullOrWhiteSpace(result) || result.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new EnvironmentIssue(
                            Severity.Warning,
                            "Python not found in PATH",
                            OperatingSystem.IsWindows()
                                ? "Install Python: winget install --id Python.Python3.11 --accept-source-agreements"
                                : "Install Python: apt-get update && apt-get install -y python3"));
                    }
                    return new EnvironmentValidation(issues.Count == 0, issues);
                }),

            // .NET
            new EnvironmentCheck(
                "dotnet",
                (exe, cmd) => exe.Contains("dotnet"),
                async (exec, dir, ct) =>
                {
                    var issues = new List<EnvironmentIssue>();
                    var result = await exec.RunCommandAsync("dotnet --version", ct);
                    if (string.IsNullOrWhiteSpace(result) || result.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new EnvironmentIssue(
                            Severity.Warning,
                            ".NET SDK not found",
                            OperatingSystem.IsWindows()
                                ? "Install .NET: winget install Microsoft.DotNet.SDK.8"
                                : "Install .NET: https://dotnet.microsoft.com/download"));
                    }
                    return new EnvironmentValidation(issues.Count == 0, issues);
                }),

            // Node.js / npm
            new EnvironmentCheck(
                "node",
                (exe, cmd) => exe.Contains("node") || exe.Contains("npm") || exe.Contains("npx"),
                async (exec, dir, ct) =>
                {
                    var issues = new List<EnvironmentIssue>();
                    var result = await exec.RunCommandAsync("node --version", ct);
                    if (string.IsNullOrWhiteSpace(result) || result.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new EnvironmentIssue(
                            Severity.Warning,
                            "Node.js not found",
                            OperatingSystem.IsWindows()
                                ? "Install Node.js: winget install OpenJS.NodeJS"
                                : "Install Node.js: https://nodejs.org"));
                    }
                    return new EnvironmentValidation(issues.Count == 0, issues);
                }),

            // Docker
            new EnvironmentCheck(
                "docker",
                (exe, cmd) => exe.Contains("docker"),
                async (exec, dir, ct) =>
                {
                    var issues = new List<EnvironmentIssue>();
                    var result = await exec.RunCommandAsync("docker version", ct);
                    if (string.IsNullOrWhiteSpace(result) || result.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                        result.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new EnvironmentIssue(
                            Severity.Warning,
                            "Docker not available or not running",
                            "Start Docker Desktop or run: systemctl start docker"));
                    }
                    return new EnvironmentValidation(issues.Count == 0, issues);
                }),

            // Git
            new EnvironmentCheck(
                "git",
                (exe, cmd) => exe.Contains("git"),
                async (exec, dir, ct) =>
                {
                    var issues = new List<EnvironmentIssue>();
                    var result = await exec.RunCommandAsync("git --version", ct);
                    if (string.IsNullOrWhiteSpace(result) || result.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new EnvironmentIssue(
                            Severity.Warning,
                            "Git not found",
                            OperatingSystem.IsWindows()
                                ? "Install Git: winget install Git.Git"
                                : "Install Git: apt-get install -y git"));
                    }
                    return new EnvironmentValidation(issues.Count == 0, issues);
                }),

            // PostgreSQL client
            new EnvironmentCheck(
                "psql",
                (exe, cmd) => exe.Contains("psql") || exe.Contains("pg_"),
                async (exec, dir, ct) =>
                {
                    var issues = new List<EnvironmentIssue>();
                    var result = await exec.RunCommandAsync("psql --version", ct);
                    if (string.IsNullOrWhiteSpace(result) || result.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new EnvironmentIssue(
                            Severity.Warning,
                            "PostgreSQL client tools not found",
                            OperatingSystem.IsWindows()
                                ? "Install PostgreSQL: winget install PostgreSQL.PostgreSQL"
                                : "Install: apt-get install -y postgresql-client"));
                    }
                    return new EnvironmentValidation(issues.Count == 0, issues);
                }),

            // PowerShell
            new EnvironmentCheck(
                "powershell",
                (exe, cmd) => exe.Contains("powershell") || exe.Contains("pwsh"),
                async (exec, dir, ct) =>
                {
                    var issues = new List<EnvironmentIssue>();
                    var result = await exec.RunCommandAsync("powershell -Command \"$PSVersionTable.PSVersion\"", ct);
                    if (string.IsNullOrWhiteSpace(result) || result.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new EnvironmentIssue(
                            Severity.Warning,
                            "PowerShell not found",
                            OperatingSystem.IsWindows()
                                ? "Install PowerShell: winget install Microsoft.PowerShell"
                                : "Install PowerShell: https://github.com/PowerShell/PowerShell"));
                    }
                    return new EnvironmentValidation(issues.Count == 0, issues);
                }),
        ];
    }

    private sealed record EnvironmentCheck(
        string Name,
        Func<string, string, bool> Matches,
        Func<IShellExecutor, string, CancellationToken, Task<EnvironmentValidation>> Validate);
}