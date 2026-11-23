using System.Net;
using System.Net.Sockets;
using Moq;
using Nebula.Agent.Infrastructure;
using Nebula.Runner;

namespace Nebula.Agent.Test;

public sealed class PortDetectionServiceTest
{
    [Fact]
    public async Task must_report_bound_port_as_in_use()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var service = CreateService();

        Assert.True(await service.IsPortInUseAsync(port));
    }

    [Fact]
    public async Task must_report_released_port_as_free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var service = CreateService();

        Assert.False(await service.IsPortInUseAsync(port));
    }

    [Fact]
    public async Task scan_must_mark_requested_ports()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var service = CreateService();

        var result = await service.ScanPortsAsync(
            Path.GetTempPath(),
            [port]);

        Assert.True(result.Success);
        var info = Assert.Single(result.Ports);
        Assert.Equal(port, info.Port);
        Assert.True(info.InUse);
    }

    [Fact]
    public async Task suggest_must_return_requested_port_when_free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var service = CreateService();

        var suggestion = await service.SuggestAlternativePortAsync(port);

        Assert.Equal(port, suggestion.RequestedPort);
        Assert.Contains(port, suggestion.AvailableAlternatives);
    }

    private static PortDetectionService CreateService()
    {
        var executor = new Mock<IShellExecutor>();
        executor
            .Setup(value => value.RunCommandAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        return new PortDetectionService(executor.Object);
    }

    private sealed class TestLogger : Nebula.Agent.ILogger
    {
        public void Log(string message)
        {
        }

        public void LogError(string message)
        {
        }
    }
}
