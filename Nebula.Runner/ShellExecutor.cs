using System.Diagnostics;

namespace Nebula.Runner;

public class ShellExecutor : IShellExecutor
{
    public async Task<string> RunCommandAsync(string command)
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

        process.Start();

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return string.IsNullOrWhiteSpace(error) ? output : error;
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