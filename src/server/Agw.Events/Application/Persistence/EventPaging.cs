using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Pagination;
using Microsoft.EntityFrameworkCore;

namespace Agw.Events.Application.Persistence;

public static class EventPaging
{
    public static async Task<PagedResult<TItem>> ToPagedResultAsync<TSource, TItem>(
        IQueryable<TSource> ordered,
        int pageIndex,
        int pageSize,
        Func<TSource, TItem> map,
        CancellationToken cancellationToken
    )
    {
        PageValidation.Validate(pageIndex, pageSize, allowPageSize100: true);
        var total = await ordered.CountAsync(cancellationToken);
        var offset = ((long)pageIndex - 1) * pageSize;
        var items = offset < total ? await ordered.Skip((int)offset).Take(pageSize).ToListAsync(cancellationToken) : [];

        return new PagedResult<TItem>
        {
            Items = items.Select(map).ToArray(),
            Total = total,
            PageIndex = pageIndex,
            PageSize = pageSize,
        };
    }
}
