using DSystem.Shared.Repositories;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace DSystem.Infrastructure.Repositories;

public class EfRepository<TEntity> : IRepository<TEntity> where TEntity : class
{
    private readonly DbContext _dbContext;
    private readonly DbSet<TEntity> _dbSet;

    public EfRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
        _dbSet = dbContext.Set<TEntity>();
    }

    public IQueryable<TEntity> Queryable => _dbSet.AsQueryable();

    public Task<TEntity?> GetByIdAsync(object id)
    {
        if (id is object[] composite)
        {
            return _dbSet.FindAsync(composite).AsTask();
        }

        return _dbSet.FindAsync(id).AsTask();
    }

    public async Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>>? predicate = null)
    {
        IQueryable<TEntity> query = _dbSet;
        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        return await query.AsNoTracking().ToListAsync();
    }

    public async Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>>? predicate = null, params Expression<Func<TEntity, object>>[] includes)
    {
        IQueryable<TEntity> query = _dbSet;

        foreach (var include in includes)
        {
            query = query.Include(include);
        }

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        return await query.AsNoTracking().ToListAsync();
    }

    public async Task AddAsync(TEntity entity)
    {
        await _dbSet.AddAsync(entity);
    }

    public void Update(TEntity entity)
    {
        _dbSet.Update(entity);
    }

    public void Remove(TEntity entity)
    {
        _dbSet.Remove(entity);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
