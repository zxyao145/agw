using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Jobs.Application.Persistence;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Jobs;

public sealed class JobDeletionCoordinator : IJobDeletionCoordinator
{
    private readonly AgwDbContext _dbContext;
    private readonly IApplicationLock _applicationLock;
    private readonly IUserInfoService _userInfoService;
    private readonly ILogger<JobDeletionCoordinator> _logger;

    public JobDeletionCoordinator(
        AgwDbContext dbContext,
        IApplicationLock applicationLock,
        IUserInfoService userInfoService,
        ILogger<JobDeletionCoordinator> logger
    )
    {
        _dbContext = dbContext;
        _applicationLock = applicationLock;
        _userInfoService = userInfoService;
        _logger = logger;
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        Action<Job> ensureMutable,
        CancellationToken cancellationToken = default
    ) => await DeleteCoreAsync(id, projectId: null, ensureMutable, cancellationToken).ConfigureAwait(false) != null;

    public Task<Job?> DeleteByProjectAsync(
        Guid id,
        Guid projectId,
        Action<Job> ensureMutable,
        CancellationToken cancellationToken = default
    ) => DeleteCoreAsync(id, projectId, ensureMutable, cancellationToken);

    private async Task<Job?> DeleteCoreAsync(
        Guid id,
        Guid? projectId,
        Action<Job> ensureMutable,
        CancellationToken cancellationToken
    )
    {
        var lease = await _applicationLock.AcquireAsync(JobOutcomeLock.GetResourceName(id), cancellationToken);
        await using var deleteScope = new ApplicationLockScope(lease, cancellationToken);
        var ownerUserId = _userInfoService.RequiredUserId;

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(deleteScope.Token);
            var query = _dbContext.Jobs.Where(job => job.Id == id && job.CreateBy == ownerUserId);
            if (projectId.HasValue)
            {
                query = query.Where(job => job.ProjectId == projectId.Value);
            }

            var job = await query.FirstOrDefaultAsync(deleteScope.Token);
            if (job == null)
            {
                return null;
            }

            ensureMutable(job);
            await _dbContext.JobLogs.Where(log => log.JobId == id).ExecuteDeleteAsync(deleteScope.Token);
            _dbContext.Jobs.Remove(job);
            await _dbContext.SaveChangesAsync(deleteScope.Token);
            await transaction.CommitAsync(deleteScope.Token);
            return job;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            _logger.LogWarning(exception, "Job {JobId} changed while being deleted.", id);
            _dbContext.ChangeTracker.Clear();
            var current = await _dbContext
                .Jobs.AsNoTracking()
                .SingleOrDefaultAsync(
                    job =>
                        job.Id == id
                        && job.CreateBy == ownerUserId
                        && (projectId == null || job.ProjectId == projectId),
                    deleteScope.Token
                );
            if (current == null)
            {
                return null;
            }

            ensureMutable(current);
            throw new AgwException(ErrorCodes.JobModificationConflict);
        }
    }
}
