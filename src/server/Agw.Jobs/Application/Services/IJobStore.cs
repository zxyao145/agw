using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Application.Services;

public interface IJobStore
{
    Task<IReadOnlyList<Job>> PrefetchAsync(DateTimeOffset now, DateTimeOffset horizon, CancellationToken cancellationToken);

    Task<bool> MarkRunningAsync(Guid jobId, CancellationToken cancellationToken);

    Task MarkSucceededAsync(Guid jobId, DateTimeOffset? nextRunTime, CancellationToken cancellationToken);

    Task MarkRetryAsync(Guid jobId, DateTimeOffset nextRunTime, int retryCount, string errorMessage, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid jobId, int retryCount, string errorMessage, CancellationToken cancellationToken);

    Task AddExecutionLogAsync(Guid jobId, Guid taskId, DateTimeOffset startTime, DateTimeOffset endTime, bool success, int attempt, string? errorMessage, CancellationToken cancellationToken);
}
