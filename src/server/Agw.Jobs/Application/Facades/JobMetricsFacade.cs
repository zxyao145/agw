using Agw.Jobs.Contracts.Metrics;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Application.Facades;

public sealed class JobMetricsFacade : IJobMetricsFacade
{
    private readonly IRepository<Job> _jobRepository;

    public JobMetricsFacade(IRepository<Job> jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public async Task<JobMetrics> GetAsync(CancellationToken cancellationToken = default) =>
        new(await _jobRepository.Queryable.CountAsync(cancellationToken).ConfigureAwait(false));
}
