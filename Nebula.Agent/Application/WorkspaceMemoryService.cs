using System.Text.RegularExpressions;

using Nebula.Core.Memory;
using Nebula.Core.Operations;

namespace Nebula.Agent.Application;

public sealed partial class WorkspaceMemoryService
{
    private readonly IWorkspaceMemoryStore store;
    private readonly ILogger logger;

    public WorkspaceMemoryService(IWorkspaceMemoryStore store, ILogger logger)
    {
        this.store = store;
        this.logger = logger;
    }

    public async Task RecordSuccessfulCommandAsync(
        string workspace,
        CommandExecution execution,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) ||
            string.IsNullOrWhiteSpace(execution.Run) ||
            execution.ExitCode is not 0)
        {
            return;
        }

        var kind = execution.OperationKind == OperationKind.ScriptExecution
            ? WorkspaceMemoryKind.Script
            : WorkspaceMemoryKind.WorkingCommand;
        var key = NormalizeKey(execution.Run);

        try
        {
            var exists = await store.ExistsAsync(
                workspace,
                kind,
                key,
                cancellationToken);
            if (exists)
            {
                return;
            }

            await store.SaveAsync(
                new WorkspaceMemoryEntry(
                    Guid.NewGuid(),
                    workspace,
                    kind,
                    key,
                    execution.Run,
                    BuildEvidence(execution),
                    DateTimeOffset.UtcNow),
                cancellationToken);

            await SaveDetectedPortsAsync(
                workspace,
                execution,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[WORKSPACE-MEMORY] Store failed (non-fatal): {ex.Message}");
        }
    }

    public async Task<string> BuildSummaryAsync(
        string workspace,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return string.Empty;
        }

        try
        {
            var entries = await store.GetRecentAsync(workspace, cancellationToken: cancellationToken);
            if (entries.Count == 0)
            {
                return string.Empty;
            }

            var commands = entries
                .Where(entry => entry.Kind == WorkspaceMemoryKind.WorkingCommand)
                .Select(entry => "- " + entry.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var scripts = entries
                .Where(entry => entry.Kind == WorkspaceMemoryKind.Script)
                .Select(entry => "- " + entry.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var ports = entries
                .Where(entry => entry.Kind == WorkspaceMemoryKind.UsedPort)
                .Select(entry => "- Port " + entry.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var lines = new List<string>();
            if (ports.Any())
            {
                lines.Add("Portas que funcionaram neste workspace:");
                lines.AddRange(ports);
            }

            if (commands.Any())
            {
                lines.Add("Comandos que ja funcionaram neste workspace:");
                lines.AddRange(commands);
            }

            if (scripts.Any())
            {
                lines.Add("Scripts que ja rodaram com sucesso neste workspace:");
                lines.AddRange(scripts);
            }

            return lines.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Log($"[WORKSPACE-MEMORY] Read failed (non-fatal): {ex.Message}");
            return string.Empty;
        }
    }

    private async Task SaveDetectedPortsAsync(
        string workspace,
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        var haystack =
            $"{execution.StandardOutput}\n{execution.StandardError}";
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return;
        }

        var matches = LocalPortRegex().Matches(haystack);
        foreach (var port in matches
                     .Select(match => match.Groups[1].Value)
                     .Distinct()
                     .Take(5))
        {
            var exists = await store.ExistsAsync(
                workspace,
                WorkspaceMemoryKind.UsedPort,
                port,
                cancellationToken);
            if (!exists)
            {
                await store.SaveAsync(
                    new WorkspaceMemoryEntry(
                        Guid.NewGuid(),
                        workspace,
                        WorkspaceMemoryKind.UsedPort,
                        port,
                        port,
                        "Detected from command output.",
                        DateTimeOffset.UtcNow),
                    cancellationToken);
            }
        }
    }

    private static string NormalizeKey(string command) =>
        Regex.Replace(
            command.Trim().ToLowerInvariant(),
            @"\s+",
            " ");

    private static string BuildEvidence(CommandExecution execution)
    {
        var samples = new List<string>();
        if (!string.IsNullOrWhiteSpace(execution.StandardOutput))
        {
            samples.Add(FirstLine(execution.StandardOutput));
        }

        samples.Add($"exitCode={execution.ExitCode}");
        return string.Join(" | ", samples);
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                var maxLength = 200;
                return trimmed.Length > maxLength
                    ? trimmed[..maxLength] + "..."
                    : trimmed;
            }
        }

        return string.Empty;
    }

    [GeneratedRegex(@"(?:localhost|127\.0\.0\.1)\s*:\s*(\d{2,5})", RegexOptions.IgnoreCase)]
    private static partial Regex LocalPortRegex();
}