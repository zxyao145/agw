namespace Agw.Jobs.Contracts.Metrics;

public sealed record JobMetrics(int JobCount);

public interface IJobMetricsFacade
{
    Task<JobMetrics> GetAsync(CancellationToken cancellationToken = default);
}
