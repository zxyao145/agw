using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Agw.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Events.Application.Persistence;

public sealed class EventPagingTests
{
    [Fact]
    public async Task ToPagedResultAsync_InvalidPageIndex_ThrowsInvalidParam()
    {
        var exception = await Assert.ThrowsAsync<AgwException>(() => ReadPageAsync(0, 20));
        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal("pageIndex must be at least 1.", exception.Message);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(101)]
    public async Task ToPagedResultAsync_UnsupportedPageSize_ThrowsInvalidPageSize(int pageSize)
    {
        var exception = await Assert.ThrowsAsync<AgwException>(() => ReadPageAsync(1, pageSize));
        Assert.Equal(ErrorCodes.InvalidPageSize.Code, exception.Code);
        Assert.Equal("pageSize must be one of 10, 20, 50, or 100.", exception.Message);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task ToPagedResultAsync_SupportedPageSize_ReturnsPage(int pageSize)
    {
        var page = await ReadPageAsync(1, pageSize);
        Assert.Equal(1, page.PageIndex);
        Assert.Equal(pageSize, page.PageSize);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
    }

    private static async Task<PagedResult<EventRecord>> ReadPageAsync(int pageIndex, int pageSize)
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        return await EventPaging.ToPagedResultAsync(
            db.Events.OrderBy(row => row.Id),
            pageIndex,
            pageSize,
            static row => row,
            TestContext.Current.CancellationToken
        );
    }
}
