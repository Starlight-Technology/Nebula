using Nebula.Core.Configuration;

namespace Nebula.Core.Agent;

public sealed class AgentProviderSelector
{
    private readonly AgentRegistry _registry = new();

    public AgentDefinition? Select(AgentCapability capability, NebulaRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        return _registry.ResolveBestFor(capability, runtimeSettings);
    }

    public AgentDefinition? Select(AgentCapability capability, ProviderMode mode)
        => _registry.ResolveBestFor(capability, mode);
}
