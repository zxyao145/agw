using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Contracts;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public class AgentflowTraceAppServiceTests
{
    [Fact]
    public async Task ListAsync_ByProjectId_ReturnsOnlyMatchingTraces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectId = Guid.NewGuid();
        var otherProjectId = Guid.NewGuid();

        await using var dbContext = await BuildDbContextAsync(cancellationToken);
        await SeedTracesAsync(dbContext, new[]
        {
            BuildTrace(projectId, startTimeUtc: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)),
            BuildTrace(otherProjectId, startTimeUtc: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)),
        }, cancellationToken);

        var service = new AgentflowTraceAppService(new EfRepository<AgentflowTrace>(dbContext));
        var result = await service.ListAsync(new AgentflowTraceQuery
        {
            ProjectId = projectId,
            PageIndex = 1,
            PageSize = 10,
        }, cancellationToken);

        Assert.Single(result.Items);
        Assert.Equal(projectId, result.Items[0].ProjectId);
    }

    [Fact]
    public async Task ListAsync_ByContextId_ReturnsOnlyMatchingTraces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dbContext = await BuildDbContextAsync(cancellationToken);
        await SeedTracesAsync(dbContext, new[]
        {
            BuildTrace(projectId: Guid.NewGuid(), contextId: "ctx-a", startTimeUtc: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)),
            BuildTrace(projectId: Guid.NewGuid(), contextId: "ctx-b", startTimeUtc: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)),
        }, cancellationToken);

        var service = new AgentflowTraceAppService(new EfRepository<AgentflowTrace>(dbContext));
        var result = await service.ListAsync(new AgentflowTraceQuery
        {
            ContextId = "ctx-a",
            PageIndex = 1,
            PageSize = 10,
        }, cancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("ctx-a", result.Items[0].ContextId);
    }

    [Fact]
    public async Task ListAsync_ByAgentflowId_ReturnsOnlyMatchingTraces()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var agentflowId = Guid.NewGuid();
        await using var dbContext = await BuildDbContextAsync(cancellationToken);
        await SeedTracesAsync(dbContext, new[]
        {
            BuildTrace(projectId: Guid.NewGuid(), agentflowId: agentflowId, startTimeUtc: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)),
            BuildTrace(projectId: Guid.NewGuid(), agentflowId: Guid.NewGuid(), startTimeUtc: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero)),
        }, cancellationToken);

        var service = new AgentflowTraceAppService(new EfRepository<AgentflowTrace>(dbContext));
        var result = await service.ListAsync(new AgentflowTraceQuery
        {
            AgentflowId = agentflowId,
            PageIndex = 1,
            PageSize = 10,
        }, cancellationToken);

        Assert.Single(result.Items);
        Assert.Equal(agentflowId, result.Items[0].AgentflowId);
    }

    [Fact]
    public async Task ListAsync_ByTimeRange_IncludesBothEndpoints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fromUtc = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

        await using var dbContext = await BuildDbContextAsync(cancellationToken);
        await SeedTracesAsync(dbContext, new[]
        {
            BuildTrace(projectId: Guid.NewGuid(), startTimeUtc: new DateTimeOffset(2026, 7, 13, 9, 59, 59, TimeSpan.Zero)),
            BuildTrace(projectId: Guid.NewGuid(), startTimeUtc: new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero)),
            BuildTrace(projectId: Guid.NewGuid(), startTimeUtc: new DateTimeOffset(2026, 7, 13, 11, 30, 0, TimeSpan.Zero)),
            BuildTrace(projectId: Guid.NewGuid(), startTimeUtc: new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)),
            BuildTrace(projectId: Guid.NewGuid(), startTimeUtc: new DateTimeOffset(2026, 7, 13, 12, 0, 1, TimeSpan.Zero)),
        }, cancellationToken);

        var service = new AgentflowTraceAppService(new EfRepository<AgentflowTrace>(dbContext));
        var result = await service.ListAsync(new AgentflowTraceQuery
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            PageIndex = 1,
            PageSize = 50,
        }, cancellationToken);

        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, trace => Assert.InRange(trace.StartTimeUtc, fromUtc, toUtc));
    }

    [Fact]
    public async Task ListAsync_SortsByStartTimeUtcDescending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dbContext = await BuildDbContextAsync(cancellationToken);
        await SeedTracesAsync(dbContext, new[]
        {
            BuildTrace(projectId: Guid.NewGuid(), startTimeUtc: new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero)),
            BuildTrace(projectId: Guid.NewGuid(), startTimeUtc: new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero)),
            BuildTrace(projectId: Guid.NewGuid(), startTimeUtc: new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero)),
        }, cancellationToken);

        var service = new AgentflowTraceAppService(new EfRepository<AgentflowTrace>(dbContext));
        var result = await service.ListAsync(new AgentflowTraceQuery
        {
            PageIndex = 1,
            PageSize = 50,
        }, cancellationToken);

        Assert.Equal(
            new[] { 10, 9, 8 },
            result.Items.Select(trace => trace.StartTimeUtc.Hour).ToArray());
    }

    [Fact]
    public async Task ListAsync_WithPagination_ReturnsCorrectPageAndTotal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectId = Guid.NewGuid();
        var traces = Enumerable.Range(0, 25)
            .Select(i => BuildTrace(projectId, startTimeUtc: new DateTimeOffset(2026, 7, 13, 0, i, 0, TimeSpan.Zero)))
            .ToArray();
        await using var dbContext = await BuildDbContextAsync(cancellationToken);
        await SeedTracesAsync(dbContext, traces, cancellationToken);

        var service = new AgentflowTraceAppService(new EfRepository<AgentflowTrace>(dbContext));
        var result = await service.ListAsync(new AgentflowTraceQuery
        {
            PageIndex = 2,
            PageSize = 10,
        }, cancellationToken);

        Assert.Equal(10, result.Items.Count);
        Assert.Equal(25, result.Total);
        Assert.Equal(2, result.PageIndex);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(
            new[] { 14, 13, 12, 11, 10, 9, 8, 7, 6, 5 },
            result.Items.Select(trace => trace.StartTimeUtc.Minute).ToArray());
    }

    [Fact]
    public async Task ListAsync_MapsEntityToDto()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var trace = BuildTrace(
            projectId: Guid.NewGuid(),
            contextId: "ctx-map",
            agentflowId: Guid.NewGuid(),
            nodeId: "node-map",
            nodeName: "Map Node",
            nodeKind: AgentflowNodeKind.Agent,
            agentId: Guid.NewGuid(),
            agentName: "Map Agent",
            input: """[{"role":"user"}]""",
            durationMilliseconds: 1234L,
            status: AgentflowNodeExecutionStatus.Succeeded,
            error: null,
            startTimeUtc: new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
        await using var dbContext = await BuildDbContextAsync(cancellationToken);
        await SeedTracesAsync(dbContext, [trace], cancellationToken);

        var service = new AgentflowTraceAppService(new EfRepository<AgentflowTrace>(dbContext));
        var result = await service.ListAsync(new AgentflowTraceQuery
        {
            PageIndex = 1,
            PageSize = 10,
        }, cancellationToken);

        var dto = Assert.Single(result.Items);
        Assert.Equal(trace.Id, dto.Id);
        Assert.Equal(trace.StartTimeUtc, dto.StartTimeUtc);
        Assert.Equal(trace.ProjectId, dto.ProjectId);
        Assert.Equal(trace.ContextId, dto.ContextId);
        Assert.Equal(trace.TaskId, dto.TaskId);
        Assert.Equal(trace.AgentflowId, dto.AgentflowId);
        Assert.Equal(trace.NodeId, dto.NodeId);
        Assert.Equal(trace.NodeName, dto.NodeName);
        Assert.Equal(trace.NodeKind, dto.NodeKind);
        Assert.Equal(trace.AgentId, dto.AgentId);
        Assert.Equal(trace.AgentName, dto.AgentName);
        Assert.Equal(trace.Input, dto.Input);
        Assert.Equal(trace.DurationMilliseconds, dto.DurationMilliseconds);
        Assert.Equal(trace.Status, dto.Status);
        Assert.Equal(trace.Error, dto.Error);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    public async Task ListAsync_WithInvalidPaging_ThrowsAgwException(int pageIndex, int pageSize)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dbContext = await BuildDbContextAsync(cancellationToken);
        var service = new AgentflowTraceAppService(new EfRepository<AgentflowTrace>(dbContext));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.ListAsync(new AgentflowTraceQuery
            {
                PageIndex = pageIndex,
                PageSize = pageSize,
            }, cancellationToken));
        Assert.Equal(ErrorCodes.InvalidPageSize.Code, exception.Code);
    }

    [Fact]
    public async Task ListAsync_WithFromUtcAfterToUtc_ThrowsAgwException()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dbContext = await BuildDbContextAsync(cancellationToken);
        var service = new AgentflowTraceAppService(new EfRepository<AgentflowTrace>(dbContext));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.ListAsync(new AgentflowTraceQuery
            {
                FromUtc = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
                ToUtc = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero),
                PageIndex = 1,
                PageSize = 10,
            }, cancellationToken));
        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    private static async Task<AgwDbContext> BuildDbContextAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        return context;
    }

    private static Task SeedTracesAsync(
        AgwDbContext dbContext,
        IReadOnlyCollection<AgentflowTrace> traces,
        CancellationToken cancellationToken)
    {
        dbContext.AgentflowNodeExecutionTraces.AddRange(traces);
        return dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AgentflowTrace BuildTrace(
        Guid projectId,
        string contextId = "ctx-default",
        Guid? agentflowId = null,
        string nodeId = "node-1",
        string? nodeName = "Node",
        AgentflowNodeKind nodeKind = AgentflowNodeKind.Agent,
        Guid? agentId = null,
        string? agentName = "Agent",
        string input = "{}",
        long durationMilliseconds = 100L,
        AgentflowNodeExecutionStatus status = AgentflowNodeExecutionStatus.Succeeded,
        string? error = null,
        DateTimeOffset? startTimeUtc = null)
    {
        return new AgentflowTrace
        {
            Id = Guid.CreateVersion7(),
            StartTimeUtc = startTimeUtc ?? TimeProvider.System.GetUtcNow(),
            ProjectId = projectId,
            ContextId = contextId,
            TaskId = Guid.NewGuid(),
            AgentflowId = agentflowId ?? Guid.NewGuid(),
            NodeId = nodeId,
            NodeName = nodeName,
            NodeKind = nodeKind,
            AgentId = agentId,
            AgentName = agentName,
            Input = input,
            DurationMilliseconds = durationMilliseconds,
            Status = status,
            Error = error,
        };
    }
}
