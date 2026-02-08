using DSystem.SessionRecords.Repositories;
using Microsoft.EntityFrameworkCore;

namespace DSystem.Infrastructure.Repositories;

public class SessionRecordsUnitOfWork : ISessionRecordsUnitOfWork
{
    private readonly DbContext _dbContext;

    public SessionRecordsUnitOfWork(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<int> SaveChangesAsync() => _dbContext.SaveChangesAsync();
}
