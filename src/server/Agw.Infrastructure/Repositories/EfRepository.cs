using System.Linq.Expressions;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Repositories;

public class EfRepository<TEntity> : IRepository<TEntity>
    where TEntity : class
{
    protected readonly DbContext _dbContext;
    protected readonly DbSet<TEntity> _dbSet;

    public EfRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
        _dbSet = dbContext.Set<TEntity>();
    }

    public IQueryable<TEntity> Queryable => _dbSet.AsQueryable();

    public Task<TEntity?> GetByIdAsync(object id)
    {
        var key = _dbContext.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey();
        if (key == null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Entity '{typeof(TEntity).Name}' has no primary key.");
        }

        object?[] values = id switch
        {
            object[] composite => composite,
            Array array => array.Cast<object?>().ToArray(),
            _ => [id],
        };
        if (values.Length != key.Properties.Count)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Expected {key.Properties.Count} key value(s) for '{typeof(TEntity).Name}', but received {values.Length}."
            );
        }

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        Expression? predicate = null;
        for (var index = 0; index < key.Properties.Count; index++)
        {
            var property = key.Properties[index];
            Expression propertyAccess =
                property.PropertyInfo == null
                    ? Expression.Call(
                        typeof(EF),
                        nameof(EF.Property),
                        [property.ClrType],
                        parameter,
                        Expression.Constant(property.Name)
                    )
                    : Expression.Property(parameter, property.PropertyInfo);
            var value = Expression.Constant(values[index], property.ClrType);
            var equals = Expression.Equal(propertyAccess, value);
            predicate = predicate == null ? equals : Expression.AndAlso(predicate, equals);
        }

        return _dbSet.SingleOrDefaultAsync(Expression.Lambda<Func<TEntity, bool>>(predicate!, parameter));
    }

    public Task<TEntity?> SingleOrDefaultAsync(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default(CancellationToken)
    )
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
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy
    )
    {
        return BuildQuery(_dbSet, predicate, orderBy);
    }

    private static IQueryable<TEntity> BuildQuery(
        IQueryable<TEntity> query,
        Expression<Func<TEntity, bool>>? predicate,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy
    )
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
