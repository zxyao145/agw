using System.Linq.Expressions;

namespace Agw.Domain.Repositories;

public interface IRepository<TEntity> where TEntity : class
{
    IQueryable<TEntity> Queryable { get; }

    Task<TEntity?> GetByIdAsync(object id);

    Task<TEntity?> SingleOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default(CancellationToken));

    Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>>? predicate = null);

    Task<IReadOnlyList<TEntity>> ListAsync(Expression<Func<TEntity, bool>>? predicate = null, params Expression<Func<TEntity, object>>[] includes);

    Task AddAsync(TEntity entity);

    void Update(TEntity entity);

    void Remove(TEntity entity);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
