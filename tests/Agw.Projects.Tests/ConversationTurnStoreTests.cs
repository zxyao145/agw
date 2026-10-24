using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Projects;
using Agw.Testing;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

/// <summary>
/// 判断 Turn 是否已被会话中更新的 Turn 取代：按 (first_sequence, turnId) 排序，只看当前用户的会话。
/// Whether a turn is superseded by a newer turn of its conversation: ordered by (first_sequence, turnId) and limited to the current user's conversations.
/// </summary>
public sealed class ConversationTurnStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(
        AppContext.BaseDirectory,
        "test-databases",
        $"agw-turn-store-{Guid.NewGuid():N}.db"
    );
    private DbContextOptions<AgwDbContext> _options = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False")
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new AgwDbContext(_options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task IsSupersededAsync_TurnsInOneConversation_OnlyTheLatestIsNotSuperseded()
    {
        // Arrange: turns ordered by first_sequence, with the last two sharing one and ordered by ID.
        // 准备：按 first_sequence 排序的 Turn，最后两个共用一个 first_sequence，按 ID 排序。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        var conversationId = AddConversation(context, "tester");
        var otherConversationId = AddConversation(context, "tester");
        var first = AddTurn(context, conversationId, 0, Guid.Parse("01994000-0000-7000-8000-000000000009"));
        var second = AddTurn(context, conversationId, 2, Guid.Parse("01994000-0000-7000-8000-000000000008"));
        var sharedLater = AddTurn(context, conversationId, 4, Guid.Parse("01994000-0000-7000-8000-000000000002"));
        var sharedEarlier = AddTurn(context, conversationId, 4, Guid.Parse("01994000-0000-7000-8000-000000000001"));
        AddTurn(context, otherConversationId, 10, Guid.Parse("01994000-0000-7000-8000-00000000000a"));
        await context.SaveChangesAsync(token);
        using var user = EnterUser();
        var store = new ConversationTurnStore(context, TimeProvider.System);

        // Act
        var results = new List<bool>();
        foreach (var turnId in new[] { first, second, sharedEarlier, sharedLater })
            results.Add(await store.IsSupersededAsync(turnId, token));

        // Assert
        Assert.Equal([true, true, true, false], results);
    }

    [Fact]
    public async Task IsSupersededAsync_MissingOrForeignTurn_ReturnsFalse()
    {
        // Arrange: another user's conversation where the earlier turn is superseded.
        // 准备：其他用户的会话，其中较早的 Turn 已被取代。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        var foreignConversationId = AddConversation(context, "another-user");
        var foreignEarlier = AddTurn(context, foreignConversationId, 0, Guid.CreateVersion7());
        AddTurn(context, foreignConversationId, 2, Guid.CreateVersion7());
        await context.SaveChangesAsync(token);
        using var user = EnterUser();
        var store = new ConversationTurnStore(context, TimeProvider.System);

        // Act
        var foreign = await store.IsSupersededAsync(foreignEarlier, token);
        var missing = await store.IsSupersededAsync(Guid.CreateVersion7(), token);

        // Assert
        Assert.False(foreign);
        Assert.False(missing);
    }

    [Fact]
    public async Task FinishAsync_RunningTurn_WritesUsageSettlementTime()
    {
        // Arrange: a running turn whose usage settles one lease duration after it finishes.
        // 准备：运行中的 Turn，用量在结束后一个租约时长结算。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        var conversationId = AddConversation(context, "tester");
        var turnId = AddTurn(context, conversationId, 0, Guid.CreateVersion7(), ProjectConversationTurnStatus.Running);
        await context.SaveChangesAsync(token);
        using var user = EnterUser();
        var store = new ConversationTurnStore(context, new TestTimeProvider(Now));

        // Act
        await store.FinishAsync(
            turnId,
            ConversationTurnStatus.Interrupted,
            0,
            null,
            Now + TimeSpan.FromSeconds(30),
            token
        );

        // Assert
        var turn = await ReadTurnAsync(turnId);
        Assert.Equal(ProjectConversationTurnStatus.Interrupted, turn.Status);
        Assert.Equal(Now, turn.FinishedAt);
        Assert.Equal(Now + TimeSpan.FromSeconds(30), turn.UsageSettlesAt);
    }

    [Fact]
    public async Task SettleUsageAsync_TerminalTurnBeforeSettlement_SettlesNow()
    {
        // Arrange: an interrupted turn whose settlement time is still ahead.
        // 准备：已中断、结算时间还没有到的 Turn。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        var conversationId = AddConversation(context, "tester");
        var turnId = AddTurn(
            context,
            conversationId,
            0,
            Guid.CreateVersion7(),
            ProjectConversationTurnStatus.Interrupted,
            finishedAt: Now - TimeSpan.FromSeconds(5),
            usageSettlesAt: Now + TimeSpan.FromSeconds(25)
        );
        await context.SaveChangesAsync(token);
        using var user = EnterUser();
        var store = new ConversationTurnStore(context, new TestTimeProvider(Now));

        // Act
        await store.SettleUsageAsync(turnId, token);

        // Assert
        var turn = await ReadTurnAsync(turnId);
        Assert.Equal(Now, turn.UsageSettlesAt);
        Assert.Equal(Now - TimeSpan.FromSeconds(5), turn.FinishedAt);
    }

    [Theory]
    [InlineData(ProjectConversationTurnStatus.Running, null)]
    [InlineData(ProjectConversationTurnStatus.Completed, -10)]
    public async Task SettleUsageAsync_RunningOrSettledTurn_KeepsUsageSettlementTime(
        ProjectConversationTurnStatus status,
        int? settledSecondsFromNow
    )
    {
        // Arrange: a turn that has not finished, or whose usage has already settled.
        // 准备：尚未结束的 Turn，或者用量已经结算的 Turn。
        var token = TestContext.Current.CancellationToken;
        DateTimeOffset? usageSettlesAt = settledSecondsFromNow is { } seconds ? Now.AddSeconds(seconds) : null;
        await using var context = new AgwDbContext(_options);
        var conversationId = AddConversation(context, "tester");
        var turnId = AddTurn(
            context,
            conversationId,
            0,
            Guid.CreateVersion7(),
            status,
            finishedAt: usageSettlesAt,
            usageSettlesAt: usageSettlesAt
        );
        await context.SaveChangesAsync(token);
        using var user = EnterUser();
        var store = new ConversationTurnStore(context, new TestTimeProvider(Now));

        // Act
        await store.SettleUsageAsync(turnId, token);

        // Assert
        Assert.Equal(usageSettlesAt, (await ReadTurnAsync(turnId)).UsageSettlesAt);
    }

    [Fact]
    public async Task SettleUsageAsync_DeletedTurn_ChangesNothing()
    {
        // Arrange: the turn row was deleted when the conversation records were cleared.
        // 准备：清空对话记录时 Turn 行已经删除。
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(_options);
        using var user = EnterUser();
        var store = new ConversationTurnStore(context, new TestTimeProvider(Now));

        // Act
        var exception = await Record.ExceptionAsync(() => store.SettleUsageAsync(Guid.CreateVersion7(), token));

        // Assert
        Assert.Null(exception);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);

    private async Task<ProjectConversationTurn> ReadTurnAsync(Guid turnId)
    {
        await using var context = new AgwDbContext(_options);
        return await context
            .ProjectConversationTurns.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(turn => turn.Id == turnId, TestContext.Current.CancellationToken);
    }

    private static IDisposable EnterUser() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
        );

    private static Guid AddConversation(AgwDbContext context, string owner)
    {
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        context.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = projectId.ToString("N"),
                CreateBy = owner,
            }
        );
        context.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = conversationId,
                ProjectId = projectId,
                ContextId = conversationId.ToString("N"),
                CreateBy = owner,
            }
        );
        return conversationId;
    }

    private static Guid AddTurn(AgwDbContext context, Guid conversationId, long firstSequence, Guid turnId) =>
        AddTurn(
            context,
            conversationId,
            firstSequence,
            turnId,
            ProjectConversationTurnStatus.Completed,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch
        );

    private static Guid AddTurn(
        AgwDbContext context,
        Guid conversationId,
        long firstSequence,
        Guid turnId,
        ProjectConversationTurnStatus status,
        DateTimeOffset? finishedAt = null,
        DateTimeOffset? usageSettlesAt = null
    )
    {
        context.ProjectConversationTurns.Add(
            new ProjectConversationTurn
            {
                Id = turnId,
                ProjectConversationId = conversationId,
                TargetId = Guid.CreateVersion7(),
                RuntimeType = AgentRuntimeType.Agent,
                Status = status,
                FirstSequence = firstSequence,
                StartedAt = DateTimeOffset.UnixEpoch,
                FinishedAt = finishedAt,
                UsageSettlesAt = usageSettlesAt,
            }
        );
        return turnId;
    }
}
