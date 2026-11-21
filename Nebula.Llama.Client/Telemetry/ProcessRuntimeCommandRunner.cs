using System.Diagnostics;

namespace Nebula.Llama.Client;

public sealed class ProcessRuntimeCommandRunner : IRuntimeCommandRunner
{
    public async Task<RuntimeCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new RuntimeCommandResult(-1, string.Empty, ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            return new RuntimeCommandResult(-1, string.Empty, "Comando cancelado.");
        }

        return new RuntimeCommandResult(
            process.ExitCode,
            await stdout,
            await stderr);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
