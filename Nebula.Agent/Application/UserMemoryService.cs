using System.Text;

using Nebula.Core.Memory;

namespace Nebula.Agent.Application;

public interface IUserMemoryService
{
    string DefaultUserId { get; }

    Task SetPreferenceAsync(
        string userId,
        UserMemoryKind kind,
        string key,
        string value,
        CancellationToken cancellationToken = default);

    Task<string> BuildUserPreferencesSummaryAsync(
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed class UserMemoryService(
    IUserMemoryStore store,
    ILogger logger) : IUserMemoryService
{
    public const string DefaultUserIdConstant = "default";

    private const int MaxPreferenceValueLength = 200;

    public string DefaultUserId => DefaultUserIdConstant;

    public async Task SetPreferenceAsync(
        string userId,
        UserMemoryKind kind,
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        var valueText = (value ?? string.Empty).Trim();
        if (valueText.Length > MaxPreferenceValueLength)
        {
            valueText = valueText[..MaxPreferenceValueLength];
        }

        if (string.IsNullOrWhiteSpace(valueText))
        {
            return;
        }

        var keyText = string.IsNullOrWhiteSpace(key) ? kind.ToString() : key.Trim();
        await store.SaveAsync(
            new UserMemoryEntry(
                Guid.NewGuid(),
                userId,
                kind,
                keyText,
                valueText,
                DateTimeOffset.UtcNow),
            cancellationToken);
        logger.Log(
            $"[USER-MEMORY] Saved preference '{keyText}' ({kind}) for user '{userId}'.");
    }

    public async Task<string> BuildUserPreferencesSummaryAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var entries = await store.GetRecentAsync(userId, cancellationToken: cancellationToken);
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var lines = new StringBuilder();
        lines.AppendLine("Preferencias do usuario:");
        foreach (var entry in entries)
        {
            lines.AppendLine($"- {FriendlyName(entry.Kind, entry.Key)}: {entry.Value}");
        }

        var summary = lines.ToString().Trim();
        return summary.Length > 2048
            ? summary[..2048]
            : summary;
    }

    private static string FriendlyName(UserMemoryKind kind, string key)
    {
        if (!string.IsNullOrWhiteSpace(key) &&
            !key.Equals(kind.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return key;
        }

        return kind switch
        {
            UserMemoryKind.Language => "Idioma",
            UserMemoryKind.Style => "Estilo",
            UserMemoryKind.DetailLevel => "Nivel de detalhe",
            UserMemoryKind.AutonomyTolerance => "Tolerancia a autonomia",
            _ => kind.ToString()
        };
    }
}