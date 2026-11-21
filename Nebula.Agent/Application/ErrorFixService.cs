using System.Text;
using Nebula.Core.Learning;
using Nebula.Core.Agent;
using Nebula.Core.Memory;
using Nebula.Core.Projects;
using Nebula.Services.Learning;

namespace Nebula.Agent.Application;

public sealed class ErrorFixService(
    WorkspaceMemoryService? workspaceMemoryService,
    ILearningFromExecutionService? learningFromExecution,
    IKnowledgeQueryService? knowledgeQueryService,
    IKnowledgeSearchService? knowledgeSearchService,
    ILogger logger) : IErrorFixService
{
    private static readonly IReadOnlyList<ErrorFix> HardcodedFixes = BuildHardcodedFixes();

    public async Task<IReadOnlyList<ErrorFix>> SearchFixesAsync(
        string errorSignature,
        string failedCommand,
        string stack,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var fixes = new List<ErrorFix>();

        // Priority 1: Workspace Strategy Memory
        if (workspaceMemoryService is not null)
        {
            try
            {
                var strategiesSummary = await workspaceMemoryService.BuildStrategiesSummaryAsync(
                    workspaceRoot, stack, cancellationToken);
                if (!string.IsNullOrWhiteSpace(strategiesSummary))
                {
                    // Parse the summary to extract individual strategies
                    // The summary format is: "Strategy for X: Command Y (saved at ...)"
                    var lines = strategiesSummary.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.Contains("Strategy for") && line.Contains("Command:"))
                        {
                            var parts = line.Split("Command:");
                            if (parts.Length > 1)
                            {
                                var cmd = parts[1].Split('(')[0].Trim();
                                var desc = line.Split("Strategy for")[1].Split(':')[0].Trim();
                                fixes.Add(new ErrorFix(
                                    "WorkspaceStrategy",
                                    $"Workspace strategy: {desc}",
                                    cmd,
                                    0.95,
                                    null));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log($"[ERROR_FIX] Workspace strategy query failed: {ex.Message}");
            }
        }

        // Priority 2: Global Learning from Execution (commands that failed then succeeded)
        if (learningFromExecution is not null && knowledgeQueryService is not null)
        {
            try
            {
                var globalFixes = await SearchGlobalLearningFixesAsync(
                    knowledgeQueryService,
                    errorSignature, failedCommand, stack, cancellationToken);
                fixes.AddRange(globalFixes);
            }
            catch (Exception ex)
            {
                logger.Log($"[ERROR_FIX] Global learning query failed: {ex.Message}");
            }
        }

        // Priority 3: Knowledge Base (procedures, warnings with fixes)
        if (knowledgeSearchService is not null)
        {
            try
            {
                var kbFixes = await SearchKnowledgeBaseFixesAsync(
                    knowledgeSearchService,
                    errorSignature, failedCommand, stack, cancellationToken);
                fixes.AddRange(kbFixes);
            }
            catch (Exception ex)
            {
                logger.Log($"[ERROR_FIX] Knowledge base query failed: {ex.Message}");
            }
        }

        // Priority 4: Hardcoded patterns
        var hardcoded = HardcodedFixes
            .Where(f => MatchesErrorSignature(f, errorSignature, failedCommand))
            .ToList();
        fixes.AddRange(hardcoded);

        // Deduplicate by suggested command, keep highest confidence
        var deduplicated = fixes
            .GroupBy(f => f.SuggestedCommand, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.Confidence).First())
            .OrderByDescending(f => f.Confidence)
            .Take(10)
            .ToList();

        logger.Log($"[ERROR_FIX] Found {deduplicated.Count} fixes for signature={errorSignature}, command={failedCommand}");
        return deduplicated;
    }

    private async Task<IReadOnlyList<ErrorFix>> SearchGlobalLearningFixesAsync(
        IKnowledgeQueryService queryService,
        string errorSignature,
        string failedCommand,
        string stack,
        CancellationToken cancellationToken)
    {
        var results = new List<ErrorFix>();

        // Search for Warning items that mention this error signature
        var query = $"error {errorSignature} fix";
        var answer = await queryService.AnswerForAutomationAsync(query, cancellationToken);

        if (!string.IsNullOrWhiteSpace(answer) &&
            !answer.Contains("Nao ha conhecimento", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new ErrorFix(
                "GlobalLearning",
                $"Knowledge base suggestion for {errorSignature}",
                ExtractCommandFromAnswer(answer),
                0.7,
                null));
        }

        return results;
    }

    private async Task<IReadOnlyList<ErrorFix>> SearchKnowledgeBaseFixesAsync(
        IKnowledgeSearchService searchService,
        string errorSignature,
        string failedCommand,
        string stack,
        CancellationToken cancellationToken)
    {
        var results = new List<ErrorFix>();

        try
        {
            var hits = await searchService.SearchKnowledgeAsync(
                $"fix {errorSignature} {failedCommand}", maxResults: 5);

            foreach (var hit in hits.Take(3))
            {
                var item = hit.Item;
                if (item.Kind is KnowledgeItemKind.Procedure or KnowledgeItemKind.Warning)
                {
                    var cmd = !string.IsNullOrWhiteSpace(item.NormalizedCommand)
                        ? item.NormalizedCommand
                        : ExtractCommandFromContent(item.Content);

                    if (!string.IsNullOrWhiteSpace(cmd))
                    {
                        results.Add(new ErrorFix(
                            "KnowledgeBase",
                            item.Summary ?? item.Title,
                            cmd,
                            0.6 * hit.Score,
                            item.Hash));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.Log($"[ERROR_FIX] KB search failed: {ex.Message}");
        }

        return results;
    }

    private static string ExtractCommandFromAnswer(string answer)
    {
        // Try to extract a command from the answer
        var lines = answer.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("$ ") || trimmed.StartsWith("> ") ||
                trimmed.StartsWith("dotnet ") || trimmed.StartsWith("python ") ||
                trimmed.StartsWith("npm ") || trimmed.StartsWith("git ") ||
                trimmed.StartsWith("ls ") || trimmed.StartsWith("cat ") ||
                trimmed.StartsWith("mkdir ") || trimmed.StartsWith("cd "))
            {
                return trimmed.TrimStart('$', '>', ' ');
            }
        }
        return "See knowledge base for details";
    }

    private static string ExtractCommandFromContent(string content)
    {
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 3 && (trimmed.StartsWith("dotnet ") || trimmed.StartsWith("python ") ||
                trimmed.StartsWith("npm ") || trimmed.StartsWith("git ") ||
                trimmed.StartsWith("ls ") || trimmed.StartsWith("cat ") ||
                trimmed.StartsWith("mkdir ") || trimmed.StartsWith("cd ") ||
                trimmed.StartsWith("test ") || trimmed.StartsWith("build ")))
            {
                return trimmed;
            }
        }
        return string.Empty;
    }

    private static bool MatchesErrorSignature(ErrorFix fix, string errorSignature, string failedCommand)
    {
        var combined = $"{fix.Description} {fix.SuggestedCommand}".ToLowerInvariant();
        var sig = errorSignature.ToLowerInvariant();
        var cmd = failedCommand.ToLowerInvariant();

        return combined.Contains(sig) ||
               (sig == "command-not-found" && (combined.Contains("install") || combined.Contains("path"))) ||
               (sig == "permission-denied" && combined.Contains("sudo")) ||
               (sig == "syntax-error" && combined.Contains("syntax"));
    }

    private static IReadOnlyList<ErrorFix> BuildHardcodedFixes()
    {
        return
        [
            // Command not found
            new ErrorFix("Hardcoded", "Install missing command via package manager",
                "winget install --id Python.Python3.11 --accept-source-agreements", 0.8, null),
            new ErrorFix("Hardcoded", "Install missing command via apt",
                "apt-get update && apt-get install -y python3", 0.8, null),
            new ErrorFix("Hardcoded", "Install .NET SDK",
                "winget install Microsoft.DotNet.SDK.8", 0.8, null),
            new ErrorFix("Hardcoded", "Install Node.js",
                "winget install OpenJS.NodeJS", 0.8, null),

            // Permission denied
            new ErrorFix("Hardcoded", "Run with elevated privileges (Windows)",
                "powershell -Command \"Start-Process cmd -Verb RunAs\"", 0.7, null),
            new ErrorFix("Hardcoded", "Fix file permissions (Linux/macOS)",
                "chmod +x ./script.sh", 0.7, null),

            // Syntax error
            new ErrorFix("Hardcoded", "Check script syntax with linter",
                "python -m py_compile script.py", 0.75, null),
            new ErrorFix("Hardcoded", "Validate JSON syntax",
                "cat file.json | python -m json.tool", 0.75, null),

            // Network error
            new ErrorFix("Hardcoded", "Check network connectivity",
                "ping -c 3 8.8.8.8", 0.6, null),
            new ErrorFix("Hardcoded", "Test DNS resolution",
                "nslookup github.com", 0.6, null),

            // Timeout
            new ErrorFix("Hardcoded", "Increase command timeout",
                "timeout 300s long-running-command", 0.65, null),

            // Build/test failures
            new ErrorFix("Hardcoded", "Clean and rebuild project",
                "dotnet clean && dotnet build", 0.85, null),
            new ErrorFix("Hardcoded", "Restore NuGet packages",
                "dotnet restore", 0.85, null),
            new ErrorFix("Hardcoded", "Run tests with verbose output",
                "dotnet test --logger \"console;verbosity=detailed\"", 0.8, null),
            new ErrorFix("Hardcoded", "Format code to fix style issues",
                "dotnet format", 0.8, null),

            // Python specific
            new ErrorFix("Hardcoded", "Install Python dependencies",
                "pip install -r requirements.txt", 0.85, null),
            new ErrorFix("Hardcoded", "Create virtual environment",
                "python -m venv venv && source venv/bin/activate", 0.8, null),

            // Git issues
            new ErrorFix("Hardcoded", "Reset uncommitted changes",
                "git checkout -- .", 0.7, null),
            new ErrorFix("Hardcoded", "Pull latest changes",
                "git pull --rebase", 0.7, null),

            // Docker issues
            new ErrorFix("Hardcoded", "Restart Docker daemon",
                "systemctl restart docker", 0.6, null),
            new ErrorFix("Hardcoded", "Clean Docker resources",
                "docker system prune -f", 0.6, null),
        ];
    }
}
