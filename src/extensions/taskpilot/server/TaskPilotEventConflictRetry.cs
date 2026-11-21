using Microsoft.EntityFrameworkCore;

namespace Agw.Extensions.TaskPilot;

internal static class TaskPilotEventConflictRetry
{
    public static bool IsRetryable(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is DbUpdateConcurrencyException
            || exception.Entries.Any(entry =>
                entry.Entity
                    is TaskPilotMatter
                        or TaskPilotRound
                        or TaskPilotRoundInput
                        or TaskPilotCard
                        or TaskPilotReview
                        or TaskPilotDelivery
            );
    }
}
