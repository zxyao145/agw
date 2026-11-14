using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;

namespace Agw.Infrastructure.Events;

internal sealed class EventJobProjectLock : IEventJobProjectLock
{
    private readonly AgwDbContext _dbContext;

    public EventJobProjectLock(AgwDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> TryLockAsync(Guid projectId, CancellationToken cancellationToken) =>
        _dbContext.LockOwnedProjectAsync(projectId, UserInfoUtil.RequiredUserId, cancellationToken);
}
