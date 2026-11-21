using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Infrastructure;

public sealed class EventSourceNameRepository : IEventSourceNameRepository
{
    private readonly IEventsDbContext _db;

    public EventSourceNameRepository(IEventsDbContext db)
    {
        _db = db;
    }

    public Task<bool> ExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var owner = UserInfoUtil.RequiredUserId;
        return _db.EventSources.AnyAsync(
            source =>
                source.CreateBy == owner && source.Name == name && source.DeletedAt == null && source.Id != exceptId,
            cancellationToken
        );
    }
}
