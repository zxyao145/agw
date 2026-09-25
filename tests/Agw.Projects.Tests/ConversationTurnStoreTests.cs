using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Projects.Infrastructure;
using Agw.Shared.Data.Entities.Projects;
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

    private static Guid AddTurn(AgwDbContext context, Guid conversationId, long firstSequence, Guid turnId)
    {
        context.ProjectConversationTurns.Add(
            new ProjectConversationTurn
            {
                Id = turnId,
                ProjectConversationId = conversationId,
                TargetId = Guid.CreateVersion7(),
                RuntimeType = AgentRuntimeType.Agent,
                Status = ProjectConversationTurnStatus.Completed,
                FirstSequence = firstSequence,
                StartedAt = DateTimeOffset.UnixEpoch,
                FinishedAt = DateTimeOffset.UnixEpoch,
            }
        );
        return turnId;
    }
}
