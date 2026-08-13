using Nebula.Agent.Application;
using Nebula.Agent.Data;

namespace Nebula.Agent.Infrastructure;

internal sealed class CommandAuditService(
    ICommandRepository? commandRepository,
    ILogger logger)
{
    public async Task<StoredCommand?> SaveCommandAsync(
        Guid requestId,
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        if (commandRepository is null)
        {
            return null;
        }

        try
        {
            return await commandRepository.SaveAsync(new StoredCommand
            {
                RequestId = requestId,
                CommandId = execution.Id,
                Objective = execution.Objective,
                Command = execution.Run,
                OsType = PlatformDetector.GetCurrentOsType(),
                WorkingDirectory = string.IsNullOrWhiteSpace(execution.WorkingDirectory)
                    ? null
                    : execution.WorkingDirectory,
                Shell = execution.Shell.ToString(),
                ExitCode = execution.ExitCode,
                StandardOutput = execution.StandardOutput,
                StandardError = execution.StandardError,
                SafetyDecision = execution.SafetyDecision?.ToString(),
                ApprovedByUser = execution.ApprovedByUser,
                AutoApproved = execution.AutoApproved,
                Skipped = execution.Skipped,
                Required = execution.Required,
                ExecutedAt = execution.ExecutedAt
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError($"[AGENT] Unable to persist command '{execution.Run}': {ex.Message}");
            return null;
        }
    }

    public async Task SaveVerificationAsync(
        Guid? storedCommandId,
        CommandExecution execution,
        CancellationToken cancellationToken)
    {
        if (commandRepository is null || storedCommandId is null)
        {
            return;
        }

        try
        {
            await commandRepository.SaveVerificationAsync(new CommandVerification
            {
                CommandId = storedCommandId.Value,
                IsCorrect = execution.IsCorrect,
                IsSafe = execution.IsSafe && execution.PassedLocalSafety,
                VerificationNotes = CommandValidationService.BuildVerificationNotes(execution)
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"[AGENT] Unable to persist verification for command '{storedCommandId}': {ex.Message}");
        }
    }

    public async Task UpdateExecutionAsync(
        Guid? storedCommandId,
        bool executed,
        string? executionResult,
        CancellationToken cancellationToken)
    {
        if (commandRepository is null || storedCommandId is null)
        {
            return;
        }

        try
        {
            await commandRepository.UpdateExecutionAsync(
                storedCommandId.Value,
                executed,
                executionResult,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"[AGENT] Unable to update execution for command '{storedCommandId}': {ex.Message}");
        }
    }

    public async Task UpdateExecutionDetailsAsync(
        Guid? storedCommandId,
        CommandExecution execution,
        string? result,
        CancellationToken cancellationToken)
    {
        if (commandRepository is null || storedCommandId is null)
        {
            return;
        }

        try
        {
            await commandRepository.UpdateExecutionDetailsAsync(
                storedCommandId.Value,
                execution.Executed,
                result,
                execution.ExitCode,
                execution.StandardOutput,
                execution.StandardError,
                execution.ExecutedAt,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"[AGENT] Unable to update execution details for command '{storedCommandId}': {ex.Message}");
        }
    }
}
