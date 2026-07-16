using System.Linq.Expressions;

using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data;
using Agw.Shared.Exceptions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Pagination;

public static class UpdatedTimePagination
{
    private static readonly int[] SupportedPageSizes = [10, 20, 50];

    public static async Task<PagedResult<TEntity>> ToPagedResultAsync<TEntity>(
        IQueryable<TEntity> queryable,
        Expression<Func<TEntity, Guid>> idSelector,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
        where TEntity : BaseEntity
    {
        ValidatePaging(pageIndex, pageSize);

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
            var items = ordered
                .Skip((pageIndex - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return CreateResult(items, ordered.Count, pageIndex, pageSize);
        }
    }

    private static PagedResult<TEntity> CreateResult<TEntity>(
        IReadOnlyList<TEntity> items,
        long total,
        int pageIndex,
        int pageSize)
    {
        return new PagedResult<TEntity>
        {
            Items = items,
            Total = total,
            PageIndex = pageIndex,
            PageSize = pageSize,
        };
    }

    private static void ValidatePaging(int pageIndex, int pageSize)
    {
        if (pageIndex < 1)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "pageIndex must be at least 1.");
        }

        if (!SupportedPageSizes.Contains(pageSize))
        {
            throw new AgwException(ErrorCodes.InvalidPageSize, "pageSize must be one of 10, 20, or 50.");
        }
    }

    private static bool IsDateTimeOffsetQueryTranslationException(Exception exception)
    {
        return exception is NotSupportedException
            && exception.Message.Contains(
                "SQLite does not support expressions of type 'DateTimeOffset'",
                StringComparison.Ordinal);
    }
}
