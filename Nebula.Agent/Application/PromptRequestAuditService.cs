using Nebula.Agent.Data;
using Nebula.Core.Interactions;

namespace Nebula.Agent.Application;

internal sealed class PromptRequestAuditService(
    IPromptRequestRepository? promptRepository,
    ILogger logger)
{
    private static readonly TimeSpan PersistenceTimeout = TimeSpan.FromMilliseconds(800);

    public async Task SaveAsync(PromptRequest request, CancellationToken cancellationToken)
    {
        if (promptRepository is null)
        {
            return;
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            logger.Log(
                $"[{request.Mode.ToString().ToUpperInvariant()}] Persisting prompt request '{request.Id}'.");
            await promptRepository.SaveAsync(request, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            LogSaveCancellation(request.Id, request.Mode, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"{ModePrefix(request.Mode)} Unable to persist prompt request '{request.Id}': {ex.Message}");
        }
    }

    public async Task UpdateResponseAsync(
        Guid requestId,
        string response,
        InteractionMode mode,
        CancellationToken cancellationToken)
    {
        if (promptRepository is null)
        {
            return;
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            logger.Log(
                $"[{mode.ToString().ToUpperInvariant()}] Updating prompt response '{requestId}'.");
            await promptRepository.UpdateResponseAsync(requestId, response, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            LogUpdateCancellation(requestId, mode, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"{ModePrefix(mode)} Unable to update prompt response '{requestId}': {ex.Message}");
        }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PersistenceTimeout);
        return timeout;
    }

    private void LogSaveCancellation(
        Guid requestId,
        InteractionMode mode,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            logger.Log(
                $"{ModePrefix(mode)} Prompt persistence for '{requestId}' was cancelled with the active conversation.");
            return;
        }

        logger.LogError(
            $"{ModePrefix(mode)} Timed out while persisting prompt request '{requestId}'. Continuing with the model response.");
    }

    private void LogUpdateCancellation(
        Guid requestId,
        InteractionMode mode,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            logger.Log(
                $"{ModePrefix(mode)} Prompt response update for '{requestId}' was cancelled with the active conversation.");
            return;
        }

        logger.LogError(
            $"{ModePrefix(mode)} Timed out while updating prompt response '{requestId}'.");
    }

    private static string ModePrefix(InteractionMode mode) =>
        mode == InteractionMode.Agent ? "[AGENT]" : "[CHAT]";
}
