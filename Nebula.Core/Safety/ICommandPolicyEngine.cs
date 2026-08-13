namespace Nebula.Core.Safety;

public interface ICommandPolicyEngine
{
    Task<CommandSafetyDecision> EvaluateAsync(
        string commandText,
        CancellationToken cancellationToken = default);
}
