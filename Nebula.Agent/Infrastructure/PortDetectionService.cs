using Nebula.Core.Agent;
using Nebula.Runner;

namespace Nebula.Agent.Infrastructure;

public sealed class PortDetectionService(
    IShellExecutor executor,
    ILogger logger) : IPortDetectionService
{
    private static readonly int[] CommonPorts = 
    [
        3000, 3001, 3002,  // React, Next.js, Vite
        4200,               // Angular
        5000, 5001, 5002,   // .NET, Flask
        5166, 5167,         // .NET default
        5173, 5174,         // Vite
        5432,               // PostgreSQL
        5555,               // Various
        5672,               // RabbitMQ
        6379,               // Redis
        7157, 7158,         // .NET HTTPS
        8000, 8001,         // Python, Go
        8080, 8081, 8082,   // Common proxy/app
        8443,               // HTTPS alt
        9000, 9001,         // Various
        9229, 9230,         // Node inspector
        27017,              // MongoDB
    ];

    public async Task<PortScanResult> ScanPortsAsync(string workingDirectory, IReadOnlyList<int>? portsToCheck = null, CancellationToken cancellationToken = default)
    {
        var ports = portsToCheck ?? CommonPorts;
        var results = new List<PortInfo>();

        foreach (var port in ports)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var info = await CheckPortAsync(port, cancellationToken);
            results.Add(info);
        }

        return new PortScanResult(true, results, null);
    }

    public async Task<PortSuggestion> SuggestAlternativePortAsync(int requestedPort, CancellationToken cancellationToken = default)
    {
        if (!await IsPortInUseAsync(requestedPort, cancellationToken))
        {
            return new PortSuggestion(requestedPort, [requestedPort]);
        }

        var alternatives = new List<int>();
        
        // Try nearby ports (±10)
        for (int i = 1; i <= 10 && alternatives.Count < 5; i++)
        {
            var lower = requestedPort - i;
            var upper = requestedPort + i;

            if (lower > 0 && !await IsPortInUseAsync(lower, cancellationToken))
                alternatives.Add(lower);

            if (alternatives.Count >= 5) break;

            if (!await IsPortInUseAsync(upper, cancellationToken))
                alternatives.Add(upper);

            if (alternatives.Count >= 5) break;
        }

        // Try common alternative ports
        var commonAlternatives = new[] { 3000, 3001, 4000, 5000, 5001, 8000, 8080, 8081, 8082, 9000 };
        foreach (var port in commonAlternatives)
        {
            if (alternatives.Count >= 5) break;
            if (port != requestedPort && !await IsPortInUseAsync(port, cancellationToken))
                alternatives.Add(port);
        }

        return new PortSuggestion(requestedPort, alternatives);
    }

    public async Task<bool> IsPortInUseAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<PortInfo> CheckPortAsync(int port, CancellationToken cancellationToken)
    {
        var inUse = await IsPortInUseAsync(port, cancellationToken);
        string? processName = null;
        int? processId = null;

        if (inUse)
        {
            try
            {
                var (name, pid) = await GetProcessUsingPortAsync(port, cancellationToken);
                processName = name;
                processId = pid;
            }
            catch { }
        }

        return new PortInfo(port, "TCP", inUse, processName, processId);
    }

    private async Task<(string? Name, int? Pid)> GetProcessUsingPortAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            string cmd;
            if (OperatingSystem.IsWindows())
            {
                cmd = $"netstat -ano | findstr :{port}";
            }
            else
            {
                cmd = $"lsof -ti :{port} | head -1";
            }

            if (executor is IDetailedShellExecutor detailedExecutor)
            {
                var result = await detailedExecutor.RunCommandDetailedAsync(cmd, Environment.CurrentDirectory, cancellationToken);
                if (result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    return ParseProcessInfo(result.StandardOutput, port);
                }
            }
            else
            {
                var output = await executor.RunCommandAsync(cmd, cancellationToken);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    return ParseProcessInfo(output, port);
                }
            }
        }
        catch { }

        return (null, null);
    }

    private static (string? Name, int? Pid) ParseProcessInfo(string output, int port)
    {
        try
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (!line.Contains($":{port}")) continue;

                if (OperatingSystem.IsWindows())
                {
                    // netstat output: TCP 0.0.0.0:3000 0.0.0.0:0 LISTENING 1234
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid))
                    {
                        return (GetProcessNameById(pid), pid);
                    }
                }
                else
                {
                    // lsof output: COMMAND PID USER FD TYPE DEVICE SIZE/OFF NODE NAME
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var pid))
                    {
                        return (parts[0], pid);
                    }
                }
            }
        }
        catch { }
        return (null, null);
    }

    private static string? GetProcessNameById(int pid)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}