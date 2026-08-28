using Agw.Auth.Contracts;
using Agw.Jobs.Contracts.Metrics;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Application.Facades;

public sealed class JobMetricsFacade : IJobMetricsFacade
{
    private readonly IRepository<Job> _jobRepository;
    private readonly IUserInfoService _userInfoService;

    public JobMetricsFacade(IRepository<Job> jobRepository, IUserInfoService userInfoService)
    {
        _jobRepository = jobRepository;
        _userInfoService = userInfoService;
    }

    public async Task<JobMetrics> GetAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var count = await _jobRepository
            .Queryable.CountAsync(job => job.CreateBy == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        return new JobMetrics(count);
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;
}
