using Nebula.Agent.Data;

namespace Nebula.Agent.Application;

internal static class ActionRequestValidator
{
    private static readonly string[] ReferentialTerms =
    [
        "that", "it", "previous", "above", "same", "isso", "aquilo", "anterior",
        "mesmo", "mesma", "ele", "ela"
    ];

    private static readonly string[] UnsafePhrases =
    [
        "delete system files", "apagar arquivos do sistema", "deletar arquivos do sistema",
        "format c:", "format disk", "wipe disk", "wipe the disk", "erase the disk",
        "del /s c:\\", "system32", "mkfs", "dd if=", "cipher /w"
    ];

    private static readonly string[] DisallowedPhrases =
    [
        "steal", "exfiltrate", "keylogger", "ransomware", "malware",
        "credential theft", "roubar senha", "disable antivirus", "desativar antivirus"
    ];

    public static ActionValidationResult Validate(AgentActionRunRequest request)
    {
        var validationText = ShouldIncludeConversationContext(request.Prompt)
            ? $"{request.ChatHistoryContext}{Environment.NewLine}{request.Prompt}"
            : request.Prompt;
        var safe = IsSafe(validationText);
        var allowed = IsAllowed(validationText);
        var feasible = IsFeasible(request.Prompt);
        var failures = BuildFailures(safe, allowed, feasible);

        return new ActionValidationResult
        {
            Safe = safe,
            Allowed = allowed,
            Feasible = feasible,
            Reason = failures.Count == 0
                ? "A acao foi considerada segura, permitida e tecnicamente viavel."
                : string.Join("; ", failures)
        };
    }

    private static bool ShouldIncludeConversationContext(string userRequest)
    {
        var normalizedRequest = userRequest.ToLowerInvariant();
        return ReferentialTerms.Any(
            term => normalizedRequest.Contains(term, StringComparison.Ordinal));
    }

    private static bool IsSafe(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !PlatformDetector.IsCommandContentSafe(value))
        {
            return false;
        }

        var normalizedValue = value.ToLowerInvariant();
        return !UnsafePhrases.Any(
            phrase => normalizedValue.Contains(phrase, StringComparison.Ordinal));
    }

    private static bool IsAllowed(string value)
    {
        var normalizedValue = value.ToLowerInvariant();
        return !DisallowedPhrases.Any(
            phrase => normalizedValue.Contains(phrase, StringComparison.Ordinal));
    }

    private static bool IsFeasible(string prompt)
    {
        return !string.IsNullOrWhiteSpace(prompt) &&
               !PlatformDetector.GetCurrentOsType().Equals(
                   "Unknown",
                   StringComparison.OrdinalIgnoreCase) &&
               ComputerOperationDetector.IsOperational(prompt);
    }

    private static List<string> BuildFailures(bool safe, bool allowed, bool feasible)
    {
        var failures = new List<string>();

        AddFailure(
            failures,
            !safe,
            "a solicitacao contem padroes destrutivos ou perigosos");
        AddFailure(
            failures,
            !allowed,
            "a solicitacao viola a politica local de acoes permitidas");
        AddFailure(
            failures,
            !feasible,
            "a acao nao parece tecnicamente executavel neste ambiente");

        return failures;
    }

    private static void AddFailure(
        List<string> failures,
        bool condition,
        string message)
    {
        if (condition)
        {
            failures.Add(message);
        }
    }
}
