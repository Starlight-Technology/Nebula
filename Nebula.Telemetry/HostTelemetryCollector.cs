using Nebula.Core.Telemetry;
using Nebula.Llama.Client.Telemetry;

namespace Nebula.Telemetry;

public sealed class HostTelemetryCollector : BackgroundService
{
    private readonly IHostHardwareProbe hardwareProbe;
    private readonly DockerContainerProbe? dockerProbe;
    private readonly TelemetryOptions options;
    private readonly ILogger<HostTelemetryCollector> logger;

    private HostTelemetrySnapshot current = new()
    {
        Source = "sidecar",
        Warnings = ["Coleta inicial ainda nao concluida."]
    };

    public HostTelemetryCollector(
        IHostHardwareProbe hardwareProbe,
        DockerContainerProbe? dockerProbe,
        TelemetryOptions options,
        ILogger<HostTelemetryCollector> logger)
    {
        this.hardwareProbe = hardwareProbe;
        this.dockerProbe = dockerProbe;
        this.options = options;
        this.logger = logger;
    }

    public HostTelemetrySnapshot Current => Volatile.Read(ref current);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = hardwareProbe.Probe() with { Source = "sidecar" };

                if (dockerProbe is not null)
                {
                    var ollama = await dockerProbe.ProbeAsync(stoppingToken);
                    if (ollama is not null)
                    {
                        snapshot = snapshot with { Ollama = ollama };
                    }
                }

                Volatile.Write(ref current, snapshot);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao coletar telemetria do host.");
                Volatile.Write(ref current, new HostTelemetrySnapshot
                {
                    Source = "sidecar",
                    Warnings = [ex.Message]
                });
            }

            try
            {
                await Task.Delay(options.IntervalMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
