using Agw.Jobs.Application.Persistence;
using Agw.Jobs.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Infrastructure;

public sealed class JobRepository : IJobRepository
{
    private readonly IJobsDbContext _dbContext;

    public JobRepository(IJobsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<int> CountByOwnerAsync(string ownerUserId) =>
        _dbContext.Jobs.CountAsync(job => job.CreateBy == ownerUserId);
}
