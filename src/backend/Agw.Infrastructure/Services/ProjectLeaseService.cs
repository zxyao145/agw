using Agw.Domain.Entities;
using Agw.Infrastructure.Data;
using Agw.Jobs.Services;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Services;

public class ProjectLeaseService(LlmDbContext dbContext) : IProjectLeaseService
{
    private readonly TimeSpan _projectLeaseDuration = TimeSpan.FromSeconds(30);

    public async Task<bool> TryAcquireAsync(Guid projectId, string instanceId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(_projectLeaseDuration);

        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE project_leases
SET locked_by = {instanceId},
    locked_until_utc = {until},
    update_by = {instanceId},
    update_time = {now}
WHERE project_id = {projectId}
  AND (locked_until_utc <= {now} OR locked_by = {instanceId})
", cancellationToken);

        if (affected == 1)
        {
            return true;
        }

        try
        {
            await dbContext.ProjectLeases.AddAsync(new ProjectLease
            {
                ProjectId = projectId,
                LockedBy = instanceId,
                LockedUntilUtc = until,
                CreateBy = instanceId,
                CreateTime = now,
                UpdateBy = instanceId,
                UpdateTime = now
            }, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    public Task RenewAsync(Guid projectId, string instanceId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(_projectLeaseDuration);

        return dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE project_leases
SET locked_until_utc = {until},
    update_by = {instanceId},
    update_time = {now}
WHERE project_id = {projectId}
  AND locked_by = {instanceId}
", cancellationToken);
    }

    public Task ReleaseAsync(Guid projectId, string instanceId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        return dbContext.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE project_leases
SET locked_until_utc = {now},
    update_by = {instanceId},
    update_time = {now}
WHERE project_id = {projectId}
  AND locked_by = {instanceId}
", cancellationToken);
    }
}
