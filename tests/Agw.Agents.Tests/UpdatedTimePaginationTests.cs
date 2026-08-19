using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Pagination;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public class UpdatedTimePaginationTests
{
    [Fact]
    public async Task ToPagedResultAsync_OnSqlite_SortsByEffectiveUpdateTimeAndIdDescending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);

        var commonTime = new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero);
        var expectedIds = Enumerable
            .Range(1, 12)
            .Select(index => Guid.Parse($"00000000-0000-0000-0000-{index:D12}"))
            .OrderDescending()
            .ToArray();

        dbContext.Agents.AddRange(
            expectedIds.Select(
                (id, index) =>
                    new Agent
                    {
                        Id = id,
                        Name = $"agent-{index}",
                        DisplayName = $"Agent {index}",
                        Type = AgentType.External,
                        CreateTime = commonTime.AddDays(-1),
                        UpdateTime = commonTime,
                    }
            )
        );
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = await UpdatedTimePagination.ToPagedResultAsync(
            dbContext.Agents.AsNoTracking(),
            agent => agent.Id,
            pageIndex: 2,
            pageSize: 10,
            cancellationToken
        );

        Assert.Equal(12, result.Total);
        Assert.Equal(2, result.PageIndex);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(expectedIds.Skip(10), result.Items.Select(agent => agent.Id));
    }

    [Fact]
    public async Task ToPagedResultAsync_UsesCreateTimeWhenUpdateTimeIsMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);

        var olderUpdated = BuildAgent(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)
        );
        var newerCreated = BuildAgent(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
            null
        );

        dbContext.Agents.AddRange(olderUpdated, newerCreated);
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = await UpdatedTimePagination.ToPagedResultAsync(
            dbContext.Agents.AsNoTracking(),
            agent => agent.Id,
            pageIndex: 1,
            pageSize: 10,
            cancellationToken
        );

        Assert.Equal(new[] { newerCreated.Id, olderUpdated.Id }, result.Items.Select(agent => agent.Id));
    }

    [Fact]
    public async Task ToPagedResultAsync_WhenPageIsOutOfRange_ReturnsEmptyItemsWithRequestedMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);

        dbContext.Agents.Add(
            BuildAgent(
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
                null
            )
        );
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = await UpdatedTimePagination.ToPagedResultAsync(
            dbContext.Agents.AsNoTracking(),
            agent => agent.Id,
            pageIndex: 2,
            pageSize: 10,
            cancellationToken
        );

        Assert.Empty(result.Items);
        Assert.Equal(1, result.Total);
        Assert.Equal(2, result.PageIndex);
        Assert.Equal(10, result.PageSize);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    public async Task ToPagedResultAsync_AcceptsSupportedPageSizes(int pageSize)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);

        PagedResult<Agent> result = await UpdatedTimePagination.ToPagedResultAsync(
            dbContext.Agents.AsNoTracking(),
            agent => agent.Id,
            pageIndex: 1,
            pageSize,
            cancellationToken
        );

        Assert.Equal(pageSize, result.PageSize);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(0, 20, 400_0001)]
    [InlineData(1, 25, 400_0025)]
    public async Task ToPagedResultAsync_WithInvalidPaging_ThrowsAgwException(
        int pageIndex,
        int pageSize,
        int expectedErrorCode
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            UpdatedTimePagination.ToPagedResultAsync(
                dbContext.Agents.AsNoTracking(),
                agent => agent.Id,
                pageIndex,
                pageSize,
                cancellationToken
            )
        );

        Assert.Equal(expectedErrorCode, exception.Code);
    }

    private static Agent BuildAgent(Guid id, DateTimeOffset createTime, DateTimeOffset? updateTime)
    {
        return new Agent
        {
            Id = id,
            Name = id.ToString("N"),
            DisplayName = id.ToString("N"),
            Type = AgentType.External,
            CreateTime = createTime,
            UpdateTime = updateTime,
        };
    }

    private static async Task<AgwDbContext> CreateDbContextAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        return dbContext;
    }
}
