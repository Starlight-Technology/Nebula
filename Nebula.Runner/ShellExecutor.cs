using System.Diagnostics;

namespace Nebula.Runner;

public class ShellExecutor : IShellExecutor
{
    public async Task<string> RunCommandAsync(string command)
    {
        return await RunCommandAsync(command, CancellationToken.None);
    }

    public async Task<string> RunCommandAsync(string command, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Command: {command}");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = GetShell(),
                Arguments = GetArguments(command),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
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

            return string.IsNullOrWhiteSpace(error) ? output : error;
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

    private string GetShell()
    {
        if (OperatingSystem.IsWindows())
            return "cmd.exe";
        else if (OperatingSystem.IsLinux())
            return "/bin/bash";

        return "/bin/sh";
    }

    private string GetArguments(string command)
    {
        if (OperatingSystem.IsWindows())
            return $"/c {command}";

        return $"-c \"{command}\"";
    }
}
