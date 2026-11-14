using System.Linq.Expressions;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Pagination;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Pagination;

public static class UpdatedTimePagination
{
    public static async Task<PagedResult<TEntity>> ToPagedResultAsync<TEntity>(
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, Guid>> idSelector,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default
    )
        where TEntity : BaseEntity
    {
        PageValidation.Validate(pageIndex, pageSize);

        queryable = queryable.AsNoTracking();

        try
        {
            var total = await queryable.CountAsync(cancellationToken);
            var items = await queryable
                .OrderByDescending(entity => entity.UpdateTime ?? entity.CreateTime)
                .ThenByDescending(idSelector)
                .Skip((pageIndex - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            return CreateResult(items, total, pageIndex, pageSize);
        }
        catch (Exception exception) when (IsDateTimeOffsetQueryTranslationException(exception))
        {
            var entities = await queryable.ToListAsync(cancellationToken);
            var getId = idSelector.Compile();
            var ordered = entities
                .OrderByDescending(entity => entity.UpdateTime ?? entity.CreateTime)
                .ThenByDescending(getId)
                .ToList();
            var items = ordered.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToList();

            return CreateResult(items, ordered.Count, pageIndex, pageSize);
        }
    }

    private static PagedResult<TEntity> CreateResult<TEntity>(
        IReadOnlyList<TEntity> items,
        long total,
        int pageIndex,
        int pageSize
    )
    {
        return new PagedResult<TEntity>
        {
            Items = items,
            Total = total,
            PageIndex = pageIndex,
            PageSize = pageSize,
        };
    }

    private static bool IsDateTimeOffsetQueryTranslationException(Exception exception)
    {
        return exception is NotSupportedException
            && exception.Message.Contains(
                "SQLite does not support expressions of type 'DateTimeOffset'",
                StringComparison.Ordinal
            );
    }
}
