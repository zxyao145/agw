using System.Linq.Expressions;

using Agw.Shared.Data.Repositories;

using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Repositories;

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

    public Task<TEntity?> SingleOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default(CancellationToken))
    {
        return _dbSet.SingleOrDefaultAsync(predicate);
    }

    public async Task<IReadOnlyList<TEntity>> ListAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null
        )
    {
        var query = BuildQuery(predicate, orderBy);
        return await query.AsNoTracking().ToListAsync();
    }

    public async Task<IReadOnlyList<TEntity>> ListAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        params Expression<Func<TEntity, object>>[] includes
        )
    {
        IQueryable<TEntity> query = _dbSet;

        foreach (var include in includes)
        {
            query = query.Include(include);
        }

        query = BuildQuery(query, predicate, orderBy);

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

    private IQueryable<TEntity> BuildQuery(
        Expression<Func<TEntity, bool>>? predicate,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy)
    {
        return BuildQuery(_dbSet, predicate, orderBy);
    }

    private static IQueryable<TEntity> BuildQuery(
        IQueryable<TEntity> query,
        Expression<Func<TEntity, bool>>? predicate,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy)
    {
        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        if (orderBy != null)
        {
            query = orderBy(query);
        }

        return query;
    }
}
