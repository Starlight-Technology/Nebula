using Nebula.Agent.Data;

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
            await promptRepository.SaveAsync(request, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            LogSaveCancellation(request.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to persist prompt request '{request.Id}': {ex.Message}");
        }
    }

    public async Task UpdateResponseAsync(
        Guid requestId,
        string response,
        CancellationToken cancellationToken)
    {
        if (promptRepository is null)
        {
            return;
        }

        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            await promptRepository.UpdateResponseAsync(requestId, response, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            LogUpdateCancellation(requestId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError($"Unable to update prompt response '{requestId}': {ex.Message}");
        }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PersistenceTimeout);
        return timeout;
    }

    private void LogSaveCancellation(Guid requestId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            logger.Log($"Prompt persistence for '{requestId}' was cancelled with the active conversation.");
            return;
        }

        logger.LogError(
            $"Timed out while persisting prompt request '{requestId}'. Continuing with the model response.");
    }

    private void LogUpdateCancellation(Guid requestId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            logger.Log($"Prompt response update for '{requestId}' was cancelled with the active conversation.");
            return;
        }

        logger.LogError($"Timed out while updating prompt response '{requestId}'.");
    }
}
