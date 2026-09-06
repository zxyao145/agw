using Agw.Auth.Contracts;
using Agw.Jobs.Application.Persistence;
using Agw.Jobs.Contracts.Metrics;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Application.Facades;

public sealed class JobMetricsFacade : IJobMetricsFacade
{
    private readonly IJobsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public JobMetricsFacade(IJobsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public async Task<JobMetrics> GetAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var count = await _dbContext
            .Jobs.CountAsync(job => job.CreateBy == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        return new JobMetrics(count);
    }

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;
}
