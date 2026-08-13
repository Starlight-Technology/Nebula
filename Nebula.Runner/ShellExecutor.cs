using System.Diagnostics;
using System.Text;

using Nebula.Core.Commands;

namespace Nebula.Runner;

public class ShellExecutor : IResolvedCommandExecutor, IStreamingShellExecutor
{
    private readonly IRuntimeCommandEnvironmentDetector environmentDetector;
    private readonly InteractivePromptDetector promptDetector;

    private static readonly TimeSpan InteractivePromptGracePeriod = TimeSpan.FromMilliseconds(250);

    public ShellExecutor(
        IRuntimeCommandEnvironmentDetector? environmentDetector = null,
        InteractivePromptDetector? promptDetector = null)
    {
        this.environmentDetector =
            environmentDetector ?? new RuntimeCommandEnvironmentDetector();
        this.promptDetector = promptDetector ?? new InteractivePromptDetector();
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
        return await RunCommandDetailedAsync(command, null, cancellationToken);
    }

    public async Task<ShellCommandResult> RunCommandDetailedAsync(
        ResolvedCommand command,
        IShellOutputObserver? observer,
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
                RedirectStandardInput = true,
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
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            var aborted = 0;

            void KillIfPrompted(StringBuilder builder)
            {
                if (!promptDetector.EndsWithInteractivePrompt(builder))
                {
                    return;
                }

                if (Interlocked.Exchange(ref aborted, 1) != 0)
                {
                    return;
                }

                Task.Delay(InteractivePromptGracePeriod).Wait();
                if (process.HasExited)
                {
                    return;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited while the grace period was elapsing.
                }
            }

            var outputTask = ReadStreamWithPromptDetectionAsync(
                process.StandardOutput,
                stdoutBuilder,
                KillIfPrompted,
                cancellationToken,
                observer,
                isError: false);
            var errorTask = ReadStreamWithPromptDetectionAsync(
                process.StandardError,
                stderrBuilder,
                KillIfPrompted,
                cancellationToken,
                observer,
                isError: true);

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;

            if (Volatile.Read(ref aborted) == 1)
            {
                const string message =
                    "Prompt interativo detectado: o comando esta esperando entrada manual " +
                    "e foi encerrado. O agente nao pode fornecer input; reformule o comando " +
                    "para nao exigir interacao (ex.: adicione flags nao-interativas).";
                return new ShellCommandResult
                {
                    Command = command.DisplayCommand,
                    WorkingDirectory = command.WorkingDirectory,
                    StandardOutput = message + Environment.NewLine + output,
                    StandardError = error,
                    ExitCode = -1,
                    Timestamp = DateTimeOffset.UtcNow
                };
            }

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

    private static async Task<string> ReadStreamWithPromptDetectionAsync(
        StreamReader reader,
        StringBuilder builder,
        Action<StringBuilder> onPromptDetected,
        CancellationToken cancellationToken,
        IShellOutputObserver? observer = null,
        bool isError = false)
    {
        var encoding = reader.CurrentEncoding;
        var buffer = new byte[4096];
        var pending = new StringBuilder();
        while (true)
        {
            var read = await reader.BaseStream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var chunk = encoding.GetString(buffer, 0, read);
            builder.Append(chunk);
            pending.Append(chunk);
            onPromptDetected(builder);

            if (observer is not null)
            {
                EmitCompleteLines(observer, pending, isError, flush: false);
            }
        }

        if (observer is not null)
        {
            EmitCompleteLines(observer, pending, isError, flush: true);
        }

        return builder.ToString();
    }

    private static void EmitCompleteLines(
        IShellOutputObserver observer,
        StringBuilder pending,
        bool isError,
        bool flush)
    {
        var text = pending.ToString();
        int newline;
        while ((newline = text.IndexOf('\n')) >= 0)
        {
            var rawLine = text[..(newline + 1)];
            text = text[(newline + 1)..];
            var line = rawLine.TrimEnd('\r', '\n');
            if (line.Length > 0)
            {
                observer.OnOutput(line, isError);
            }
        }

        if (flush && text.Length > 0)
        {
            observer.OnOutput(text.TrimEnd('\r'), isError);
            text = string.Empty;
        }

        pending.Clear();
        if (text.Length > 0)
        {
            pending.Append(text);
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
