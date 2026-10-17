using Agw.Shared.Data.Entities.Jobs;
using Microsoft.Extensions.Logging;

namespace Agw.Jobs.Scheduling.Coordination;

internal static class JobRecoverySupport
{
    public static async Task<bool> TryRecoverAsync(
        Func<CancellationToken, Task> recoverAsync,
        ILogger logger,
        string executionMode,
        CancellationToken stoppingToken
    )
    {
        try
        {
            await recoverAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to recover {ExecutionMode} Job executions.", executionMode);
        }

        return true;
    }

    public static Guid? GetActiveTurnId(Job job, ILogger logger)
    {
        if (job.ActiveTurnId is not Guid activeTurnId || !job.ActiveAttemptStartedAt.HasValue)
        {
            logger.LogError("Running Job {JobId} does not have a persisted active attempt.", job.Id);
            return null;
        }

        return activeTurnId;
    }
}
