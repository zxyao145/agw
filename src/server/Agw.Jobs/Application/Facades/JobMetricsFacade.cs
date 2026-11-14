using Agw.Events.Contracts;
using Agw.Jobs.Contracts.Metrics;

namespace Agw.Jobs.Application.Facades;

public sealed class JobMetricsFacade : IJobMetricsFacade
{
    private readonly IEventJobFacade _jobs;

    public JobMetricsFacade(IEventJobFacade jobs)
    {
        _jobs = jobs;
    }

    public async Task<JobMetrics> GetAsync(CancellationToken cancellationToken = default) =>
        new(await _jobs.CountAsync(cancellationToken));
}
