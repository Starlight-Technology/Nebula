using Nebula.Core.Configuration;

namespace Nebula.Core.Agent;

public enum AgentCapability
{
    Chat,
    Coding,
    Research,
    Terminal,
    FileSystem,
    Validation,
    Planning,
    Review,
    Documentation
}

public sealed record AgentDefinition(
    string Id,
    string Name,
    string Description,
    ProviderMode PreferredMode,
    IReadOnlyList<AgentCapability> Capabilities,
    bool IsLocal,
    bool IsEnabled = true)
{
    public bool Supports(AgentCapability capability) => Capabilities.Contains(capability);
}

public sealed class AgentRegistry
{
    private readonly List<AgentDefinition> _agents =
    [
        new AgentDefinition(
            "coding-local",
            "Coding Local",
            "Agente local de desenvolvimento com acesso ao workspace, terminal e validacao local.",
            ProviderMode.LocalOnly,
            [AgentCapability.Coding, AgentCapability.FileSystem, AgentCapability.Terminal, AgentCapability.Validation, AgentCapability.Review],
            IsLocal: true),
        new AgentDefinition(
            "research-local",
            "Research Local",
            "Agente local de pesquisa e contextualizacao sem dependencia de APIs externas.",
            ProviderMode.LocalPreferred,
            [AgentCapability.Research, AgentCapability.Documentation, AgentCapability.Validation],
            IsLocal: true),
        new AgentDefinition(
            "planning-local",
            "Planning Local",
            "Agente de planejamento e decomposicao de tarefas para execucao local segura.",
            ProviderMode.LocalPreferred,
            [AgentCapability.Planning, AgentCapability.Review],
            IsLocal: true),
        new AgentDefinition(
            "coding-remote",
            "Coding Remote",
            "Adaptador opcional para agentes externos ou modelos remotos quando o usuario habilita isso.",
            ProviderMode.RemoteAllowed,
            [AgentCapability.Coding, AgentCapability.FileSystem, AgentCapability.Terminal, AgentCapability.Validation],
            IsLocal: false),
        new AgentDefinition(
            "research-remote",
            "Research Remote",
            "Adaptador opcional para pesquisa remota ou agentes externos quando configurado.",
            ProviderMode.RemoteAllowed,
            [AgentCapability.Research, AgentCapability.Documentation],
            IsLocal: false)
    ];

    public IReadOnlyList<AgentDefinition> All => _agents.AsReadOnly();

    public AgentDefinition? ResolveBestFor(AgentCapability capability, ProviderMode mode)
    {
        var candidates = _agents
            .Where(agent => agent.IsEnabled)
            .Where(agent => agent.Supports(capability))
            .Where(agent => IsCompatibleWithMode(agent, mode))
            .OrderByDescending(agent => agent.IsLocal)
            .ThenBy(agent => agent.PreferredMode == mode ? 0 : 1)
            .ThenBy(agent => agent.Id)
            .ToList();

        return candidates.FirstOrDefault();
    }

    public AgentDefinition? ResolveBestFor(AgentCapability capability, NebulaRuntimeSettings runtimeSettings)
        => ResolveBestFor(capability, runtimeSettings.ProviderMode);

    private static bool IsCompatibleWithMode(AgentDefinition agent, ProviderMode mode)
    {
        return mode switch
        {
            ProviderMode.LocalOnly => agent.IsLocal,
            ProviderMode.LocalPreferred => agent.IsLocal || agent.PreferredMode is ProviderMode.LocalPreferred or ProviderMode.RemoteAllowed,
            ProviderMode.RemoteAllowed => true,
            ProviderMode.RemoteOnly => !agent.IsLocal || agent.PreferredMode == ProviderMode.RemoteOnly,
            _ => true
        };
    }
}
