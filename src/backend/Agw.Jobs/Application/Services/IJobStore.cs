using Agw.Jobs.Domain.Entities;

namespace Agw.Jobs.Application.Services;

public interface IJobStore
{
    Task<IReadOnlyList<Job>> PrefetchAsync(DateTimeOffset now, DateTimeOffset horizon, CancellationToken cancellationToken);

    Task<bool> MarkRunningAsync(Guid taskId, CancellationToken cancellationToken);

    Task MarkSucceededAsync(Guid taskId, DateTimeOffset? nextRunTime, CancellationToken cancellationToken);

    Task MarkRetryAsync(Guid taskId, DateTimeOffset nextRunTime, int retryCount, string errorMessage, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid taskId, int retryCount, string errorMessage, CancellationToken cancellationToken);

    Task AddExecutionLogAsync(Guid taskId, DateTimeOffset startTime, DateTimeOffset endTime, bool success, int attempt, string? errorMessage, CancellationToken cancellationToken);
}
