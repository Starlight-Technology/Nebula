using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Nebula.App.Shared.Setup;

public interface IRuntimeSetupAdvisor
{
    RuntimeSetupRecommendation BuildRecommendation(ClientEnvironmentProbe? probe, string runtimeUrl);
}

public sealed class RuntimeSetupAdvisor(string shellKind) : IRuntimeSetupAdvisor
{
    public RuntimeSetupRecommendation BuildRecommendation(ClientEnvironmentProbe? probe, string runtimeUrl)
    {
        var context = CreateRecommendationContext(probe, runtimeUrl);
        return SelectRecommendation(context);
    }

    private RecommendationContext CreateRecommendationContext(
        ClientEnvironmentProbe? probe,
        string runtimeUrl)
    {
        var host = CaptureHostSnapshot(shellKind);
        var client = CreateClientSnapshot(probe);
        var runtime = CreateRuntimeTarget(runtimeUrl);
        var sameMachine = host.ShellKind == "Native app" || ClientMatchesHost(client, host);
        var gpuKind = sameMachine ? DetectGpuKind(probe) : GpuKind.Unknown;

        var reasons = new List<string>();
        var nextSteps = new List<string>();
        var warnings = new List<string>();

        if (!sameMachine && client is not null)
        {
            warnings.Add("A UI parece rodar em outro dispositivo. A pista de GPU do navegador foi ignorada para evitar uma recomendacao errada.");
        }

        if (!runtime.IsLocal)
        {
            warnings.Add($"O endpoint atual do Ollama parece remoto ({runtime.Host}). A sugestao abaixo so faz sentido se o runtime morar na mesma maquina desta central.");
        }

        var context = new RecommendationContext(
            host,
            client,
            runtime,
            probe,
            sameMachine,
            gpuKind,
            reasons,
            nextSteps,
            warnings);

        return context;
    }

    private static RuntimeSetupRecommendation SelectRecommendation(
        RecommendationContext context)
    {
        if (context.Host.IsMacOS)
        {
            return BuildMacRecommendation(context);
        }

        if (context.GpuKind == GpuKind.Nvidia)
        {
            return BuildNvidiaRecommendation(context);
        }

        if (ShouldRecommendAmd(context))
        {
            return BuildAmdRecommendation(context);
        }

        if (ShouldRecommendIntel(context))
        {
            return BuildIntelRecommendation(context);
        }

        var warnings = context.Warnings;
        var nextSteps = context.NextSteps;
        context.Reasons.Add("Nao encontrei sinais confiaveis o bastante para recomendar um backend de GPU com seguranca.");
        context.Reasons.Add("Neste cenario, CPU continua sendo o caminho mais previsivel para manter a central funcional.");

        if (HasMissingGpuDetails(context))
        {
            warnings.Add("O shell atual nao expôs informacoes detalhadas da GPU. Isso costuma acontecer quando o navegador bloqueia o renderer WebGL.");
        }

        nextSteps.Add("Comece por CPU e valide o fluxo de conversa, instalacao e troca de modelo.");
        nextSteps.Add("Se a maquina realmente tiver GPU compativel, voce pode testar manualmente um dos perfis dedicados depois.");

        return BuildCpuRecommendation(context);
    }

    private static bool ShouldRecommendAmd(RecommendationContext context)
    {
        return context.Host.IsLinux &&
               (context.GpuKind == GpuKind.Amd || context.Host.HasKfdDevice);
    }

    private static bool ShouldRecommendIntel(RecommendationContext context)
    {
        return context.Host.IsLinux &&
               (context.GpuKind == GpuKind.Intel || context.Host.HasDriDevice);
    }

    private static bool HasMissingGpuDetails(RecommendationContext context)
    {
        return context.SameMachine &&
               context.Probe is not null &&
               string.IsNullOrWhiteSpace(context.Probe.GpuRenderer) &&
               string.IsNullOrWhiteSpace(context.Probe.GpuVendor);
    }

    private static RuntimeSetupRecommendation BuildMacRecommendation(
        RecommendationContext context)
    {
        context.Reasons.Add("O host do agente esta em macOS.");
        context.Reasons.Add("Neste stack, a rota mais previsivel continua sendo CPU no Compose e Ollama nativo se voce quiser mais performance no Mac.");
        context.NextSteps.Add("Use o perfil CPU se quiser continuar dentro deste docker-compose.");
        context.NextSteps.Add("Se a prioridade for desempenho, rode o Ollama nativo no host e mantenha a central apontando para ele.");

        return context.CreateRecommendation(
            profileKey: "cpu",
            profileName: "CPU",
            command: "docker compose up -d",
            summary: "CPU e a rota mais segura neste ambiente.",
            confidence: "Alta",
            modeLabel: "Fallback estavel",
            modelHint: "Se ficar em CPU, comece por phi4-mini ou qwen3:8b para manter a experiencia leve.",
            usesGpu: false,
            isExperimental: false);
    }

    private static RuntimeSetupRecommendation BuildNvidiaRecommendation(
        RecommendationContext context)
    {
        context.Reasons.Add("Encontrei pistas de GPU NVIDIA no shell da interface.");
        context.Reasons.Add(context.Host.IsWindows
            ? "Em Windows, o caminho mais previsivel deste stack e usar Docker Desktop com WSL2 e passthrough NVIDIA."
            : "Em Linux, o perfil NVIDIA e o backend mais maduro desta central.");
        context.NextSteps.Add("Suba o runtime com o perfil NVIDIA e confirme que o host ja enxerga a GPU.");
        context.NextSteps.Add("Depois volte na central, clique em Atualizar e valide o catalogo do Ollama.");

        return context.CreateRecommendation(
            profileKey: "nvidia",
            profileName: "NVIDIA CUDA",
            command: "docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d",
            summary: "NVIDIA CUDA parece ser o melhor caminho para acelerar os modelos nesta maquina.",
            confidence: context.SameMachine ? "Alta" : "Media",
            modeLabel: "GPU recomendada",
            modelHint: "Voce pode comecar com qwen3:8b ou deepseek-r1:8b sem apertar tanto o runtime.",
            usesGpu: true,
            isExperimental: false);
    }

    private static RuntimeSetupRecommendation BuildAmdRecommendation(
        RecommendationContext context)
    {
        context.Reasons.Add(context.Host.HasKfdDevice
            ? "O host Linux expoe /dev/kfd, o que combina com o fluxo ROCm."
            : "Encontrei pistas de GPU AMD no shell da interface.");
        context.Reasons.Add("Para AMD, este projeto usa a imagem ollama/ollama:rocm com acesso aos dispositivos /dev/kfd e /dev/dri.");

        if (!context.Host.HasKfdDevice)
        {
            context.Warnings.Add("Nao encontrei /dev/kfd neste host. O perfil AMD pode precisar de ajuste no ambiente Linux antes de subir.");
        }

        context.NextSteps.Add("Confirme ROCm no host Linux e depois suba o perfil AMD.");
        context.NextSteps.Add("Se quiser reduzir risco no primeiro teste, comece com um modelo menor antes de trocar para um de raciocinio pesado.");

        return context.CreateRecommendation(
            profileKey: "amd",
            profileName: "AMD ROCm",
            command: "docker compose -f docker-compose.yml -f docker-compose.amd.yml up -d",
            summary: "AMD ROCm parece o perfil mais compativel para este host Linux.",
            confidence: context.Host.HasKfdDevice ? "Alta" : "Media",
            modeLabel: "GPU recomendada",
            modelHint: "Comece com qwen3:8b ou phi4-mini enquanto valida ROCm e depois suba para modelos mais pesados.",
            usesGpu: true,
            isExperimental: false);
    }

    private static RuntimeSetupRecommendation BuildIntelRecommendation(
        RecommendationContext context)
    {
        context.Reasons.Add(context.Host.HasDriDevice
            ? "O host Linux expoe /dev/dri, o que permite testar o backend Vulkan."
            : "Encontrei pistas de GPU Intel no shell da interface.");
        context.Reasons.Add("Neste projeto, Intel entra pelo caminho Vulkan, que ainda e experimental no Ollama.");
        context.NextSteps.Add("Suba o perfil Intel Vulkan e valide um modelo menor primeiro.");
        context.NextSteps.Add("Se o runtime ficar instavel, volte para CPU e mantenha a troca de modelos pela central.");

        return context.CreateRecommendation(
            profileKey: "intel",
            profileName: "Intel Vulkan",
            command: "docker compose -f docker-compose.yml -f docker-compose.intel.yml up -d",
            summary: "Intel Vulkan parece o melhor ponto de partida, com a ressalva de ainda ser experimental.",
            confidence: context.Host.HasDriDevice ? "Media" : "Baixa",
            modeLabel: "GPU experimental",
            modelHint: "Use phi4-mini ou qwen3:8b nas primeiras validacoes para reduzir atrito no backend Vulkan.",
            usesGpu: true,
            isExperimental: true);
    }

    private static RuntimeSetupRecommendation BuildCpuRecommendation(
        RecommendationContext context)
    {
        return context.CreateRecommendation(
            profileKey: "cpu",
            profileName: "CPU",
            command: "docker compose up -d",
            summary: "CPU e o ponto de partida mais seguro para esta maquina agora.",
            confidence: "Media",
            modeLabel: "Fallback estavel",
            modelHint: "Para CPU, priorize phi4-mini ou qwen3:8b e evite modelos grandes ate medir tempo de resposta.",
            usesGpu: false,
            isExperimental: false);
    }

    private static RuntimeHostSnapshot CaptureHostSnapshot(string shellKind)
    {
        var platform = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "macos"
                    : "unknown";

        var platformLabel = platform switch
        {
            "windows" => "Windows",
            "linux" => "Linux",
            "macos" => "macOS",
            _ => RuntimeInformation.OSDescription
        };

        var architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var architectureLabel = architecture switch
        {
            "x64" => "x64",
            "arm64" => "ARM64",
            _ => RuntimeInformation.OSArchitecture.ToString()
        };

        return new RuntimeHostSnapshot(
            shellKind,
            platform,
            platformLabel,
            architectureLabel,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            OperatingSystem.IsMacOS(),
            OperatingSystem.IsLinux() && Directory.Exists("/dev/dri"),
            OperatingSystem.IsLinux() && File.Exists("/dev/kfd"));
    }

    private static RuntimeEndpointSnapshot CreateRuntimeTarget(string runtimeUrl)
    {
        if (!Uri.TryCreate(runtimeUrl, UriKind.Absolute, out var uri))
        {
            return new RuntimeEndpointSnapshot(runtimeUrl, false);
        }

        var host = uri.Host;
        var isLocal =
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::1", StringComparison.OrdinalIgnoreCase);

        return new RuntimeEndpointSnapshot(host, isLocal);
    }

    private static ClientShellSnapshot? CreateClientSnapshot(ClientEnvironmentProbe? probe)
    {
        if (probe is null)
        {
            return null;
        }

        var platform = NormalizeClientPlatform(probe.Platform, probe.UserAgent);
        var platformLabel = platform switch
        {
            "windows" => "Windows",
            "linux" => "Linux",
            "macos" => "macOS",
            "android" => "Android",
            "ios" => "iOS",
            _ => "Nao identificado"
        };

        var browserVersion = probe.BrowserVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var browserLabel = string.IsNullOrWhiteSpace(browserVersion)
            ? probe.BrowserName
            : $"{probe.BrowserName} {browserVersion}";

        var viewportLabel = probe.ViewportWidth > 0 && probe.ViewportHeight > 0
            ? $"{probe.ViewportWidth}x{probe.ViewportHeight}"
            : "Nao informado";

        return new ClientShellSnapshot(
            platform,
            platformLabel,
            string.IsNullOrWhiteSpace(browserLabel) ? "Nao identificado" : browserLabel,
            viewportLabel,
            probe.WebGlSupported);
    }

    private static bool ClientMatchesHost(ClientShellSnapshot? client, RuntimeHostSnapshot host)
    {
        if (client is null)
        {
            return true;
        }

        return client.Platform switch
        {
            "windows" => host.IsWindows,
            "linux" => host.IsLinux,
            "macos" => host.IsMacOS,
            _ => false
        };
    }

    private static string NormalizeClientPlatform(string platform, string userAgent)
    {
        var text = $"{platform} {userAgent}".ToLowerInvariant();

        if (text.Contains("android"))
        {
            return "android";
        }

        if (text.Contains("iphone") || text.Contains("ipad") || text.Contains("ios"))
        {
            return "ios";
        }

        if (text.Contains("win"))
        {
            return "windows";
        }

        if (text.Contains("mac"))
        {
            return "macos";
        }

        if (text.Contains("linux") || text.Contains("x11"))
        {
            return "linux";
        }

        return "unknown";
    }

    private static GpuKind DetectGpuKind(ClientEnvironmentProbe? probe)
    {
        if (probe is null)
        {
            return GpuKind.Unknown;
        }

        var text = $"{probe.GpuVendor} {probe.GpuRenderer}".ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(text))
        {
            return GpuKind.Unknown;
        }

        if (ContainsAny(text, "nvidia", "geforce", "quadro", "tesla"))
        {
            return GpuKind.Nvidia;
        }

        if (ContainsAny(text, "amd", "radeon", "ati"))
        {
            return GpuKind.Amd;
        }

        if (ContainsAny(text, "intel", "iris", "arc", "xe", "uhd"))
        {
            return GpuKind.Intel;
        }

        if (text.Contains("apple"))
        {
            return GpuKind.Apple;
        }

        return GpuKind.Unknown;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(value.Contains);
    }

    private static string BuildGpuLabel(ClientEnvironmentProbe? probe, bool sameMachine, GpuKind gpuKind)
    {
        if (!sameMachine && probe is not null)
        {
            return "Ignorada porque a UI parece estar em outro dispositivo";
        }

        if (probe is null)
        {
            return "Sem leitura do shell da interface";
        }

        var renderer = probe.GpuRenderer.Trim();
        if (!string.IsNullOrWhiteSpace(renderer))
        {
            return SimplifyRendererLabel(renderer);
        }

        var vendor = probe.GpuVendor.Trim();
        if (!string.IsNullOrWhiteSpace(vendor))
        {
            return vendor;
        }

        return gpuKind switch
        {
            GpuKind.Apple => "GPU Apple detectada",
            GpuKind.Unknown => "Nao detectada",
            _ => "Detectada sem renderer detalhado"
        };
    }

    private static string SimplifyRendererLabel(string renderer)
    {
        var text = renderer.Trim();

        if (text.StartsWith("ANGLE (", StringComparison.OrdinalIgnoreCase) && text.EndsWith(')'))
        {
            var inner = text[7..^1];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var gpuName = Regex.Replace(parts[1], @"\s*\(0x[0-9A-Fa-f]+\)", string.Empty);
                var api = parts[^1];
                var directMarker = gpuName.IndexOf(" Direct", StringComparison.OrdinalIgnoreCase);
                if (directMarker >= 0)
                {
                    gpuName = gpuName[..directMarker];
                }

                return $"{gpuName} via {api}";
            }
        }

        return text.Length > 96
            ? $"{text[..93]}..."
            : text;
    }

    private static RuntimeSetupRecommendation CreateRecommendation(
        RuntimeHostSnapshot host,
        ClientShellSnapshot? client,
        RuntimeEndpointSnapshot runtime,
        string profileKey,
        string profileName,
        string command,
        string summary,
        string confidence,
        string modeLabel,
        string gpuLabel,
        string modelHint,
        bool usesGpu,
        bool isExperimental,
        List<string> reasons,
        List<string> nextSteps,
        List<string> warnings)
    {
        return new RuntimeSetupRecommendation(
            host,
            client,
            runtime,
            profileKey,
            profileName,
            command,
            summary,
            confidence,
            modeLabel,
            gpuLabel,
            modelHint,
            usesGpu,
            isExperimental,
            reasons,
            nextSteps,
            warnings);
    }

    private enum GpuKind
    {
        Unknown,
        Nvidia,
        Amd,
        Intel,
        Apple
    }

    private sealed class RecommendationContext(
        RuntimeHostSnapshot host,
        ClientShellSnapshot? client,
        RuntimeEndpointSnapshot runtime,
        ClientEnvironmentProbe? probe,
        bool sameMachine,
        GpuKind gpuKind,
        List<string> reasons,
        List<string> nextSteps,
        List<string> warnings)
    {
        public RuntimeHostSnapshot Host { get; } = host;

        public ClientShellSnapshot? Client { get; } = client;

        public RuntimeEndpointSnapshot Runtime { get; } = runtime;

        public ClientEnvironmentProbe? Probe { get; } = probe;

        public bool SameMachine { get; } = sameMachine;

        public GpuKind GpuKind { get; } = gpuKind;

        public List<string> Reasons { get; } = reasons;

        public List<string> NextSteps { get; } = nextSteps;

        public List<string> Warnings { get; } = warnings;

        public RuntimeSetupRecommendation CreateRecommendation(
            string profileKey,
            string profileName,
            string command,
            string summary,
            string confidence,
            string modeLabel,
            string modelHint,
            bool usesGpu,
            bool isExperimental)
        {
            return RuntimeSetupAdvisor.CreateRecommendation(
                Host,
                Client,
                Runtime,
                profileKey,
                profileName,
                command,
                summary,
                confidence,
                modeLabel,
                BuildGpuLabel(Probe, SameMachine, GpuKind),
                modelHint,
                usesGpu,
                isExperimental,
                Reasons,
                NextSteps,
                Warnings);
        }
    }
}

public sealed record RuntimeHostSnapshot(
    string ShellKind,
    string Platform,
    string PlatformLabel,
    string ArchitectureLabel,
    bool IsWindows,
    bool IsLinux,
    bool IsMacOS,
    bool HasDriDevice,
    bool HasKfdDevice);

public sealed record ClientShellSnapshot(
    string Platform,
    string PlatformLabel,
    string BrowserLabel,
    string ViewportLabel,
    bool WebGlSupported);

public sealed record RuntimeEndpointSnapshot(string Host, bool IsLocal);

public sealed record RuntimeSetupRecommendation(
    RuntimeHostSnapshot Host,
    ClientShellSnapshot? Client,
    RuntimeEndpointSnapshot Runtime,
    string ProfileKey,
    string ProfileName,
    string Command,
    string Summary,
    string ConfidenceLabel,
    string ModeLabel,
    string GpuLabel,
    string ModelHint,
    bool UsesGpu,
    bool IsExperimental,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<string> Warnings);
