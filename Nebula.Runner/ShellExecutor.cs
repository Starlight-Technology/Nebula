using System.Diagnostics;

using Nebula.Core.Commands;

namespace Nebula.Runner;

public class ShellExecutor : IResolvedCommandExecutor
{
    private readonly IRuntimeCommandEnvironmentDetector environmentDetector;

    public ShellExecutor(IRuntimeCommandEnvironmentDetector? environmentDetector = null)
    {
        this.environmentDetector =
            environmentDetector ?? new RuntimeCommandEnvironmentDetector();
    }

    public async Task<string> RunCommandAsync(string command)
    {
        return await RunCommandAsync(command, CancellationToken.None);
    }

    public async Task<string> RunCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        var result = await RunCommandDetailedAsync(
            command,
            Environment.CurrentDirectory,
            cancellationToken);
        return result.CombinedOutput;
    }

    public async Task<ShellCommandResult> RunCommandDetailedAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var environment = environmentDetector.Detect(workingDirectory);
        var resolved = ResolveLegacyCommand(command, environment);
        return await RunCommandDetailedAsync(resolved, cancellationToken);
    }

    public async Task<ShellCommandResult> RunCommandDetailedAsync(
        ResolvedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);
        Console.WriteLine($"Command: {command.DisplayCommand}");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = command.WorkingDirectory
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start shell process.");
        }

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;

            return new ShellCommandResult
            {
                Command = command.DisplayCommand,
                WorkingDirectory = command.WorkingDirectory,
                StandardOutput = output,
                StandardError = error,
                ExitCode = process.ExitCode,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited after HasExited was checked.
                }
            }

            throw;
        }
    }

    private static ResolvedCommand ResolveLegacyCommand(
        string command,
        RuntimeCommandEnvironment environment)
    {
        return environment.Shell switch
        {
            ShellKind.PowerShell => new ResolvedCommand(
                RuntimeCommandEnvironmentDetector.GetPowerShellExecutable()
                    ?? "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{Escape(command)}\"",
                command,
                environment.WorkingDirectory,
                ["Legacy command executed with detected PowerShell."]),
            ShellKind.Cmd => new ResolvedCommand(
                "cmd.exe",
                $"/c {command}",
                command,
                environment.WorkingDirectory,
                ["Legacy command executed with detected CMD."]),
            ShellKind.Bash => new ResolvedCommand(
                "/bin/bash",
                $"-c \"{Escape(command)}\"",
                command,
                environment.WorkingDirectory,
                ["Legacy command executed with detected bash."]),
            _ => new ResolvedCommand(
                "/bin/sh",
                $"-c \"{Escape(command)}\"",
                command,
                environment.WorkingDirectory,
                ["Legacy command executed with detected sh."])
        };
    }

    private static string Escape(string command) =>
        command.Replace("\"", "\\\"", StringComparison.Ordinal);
}
