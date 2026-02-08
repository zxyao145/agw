using DSystem.SessionRecords.Entities;
using DSystem.SessionRecords.Repositories;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace DSystem.Infrastructure.Repositories;

public class AgentSessionRecordRepository : IAgentSessionRecordRepository
{
    private readonly DbSet<AgentSessionRecord> _dbSet;

    public AgentSessionRecordRepository(DbContext dbContext)
    {
        _dbSet = dbContext.Set<AgentSessionRecord>();
    }

    public async Task<IReadOnlyList<AgentSessionRecord>> ListAsync(Expression<Func<AgentSessionRecord, bool>>? predicate = null)
    {
        IQueryable<AgentSessionRecord> query = _dbSet;
        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        return await query.AsNoTracking().ToListAsync();
    }

    public async Task AddAsync(AgentSessionRecord entity)
    {
        await _dbSet.AddAsync(entity);
    }

    public void Update(AgentSessionRecord entity)
    {
        _dbSet.Update(entity);
    }

    public void Remove(AgentSessionRecord entity)
    {
        _dbSet.Remove(entity);
    }
}
