using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.Facades;
using Agw.Shared.Contracts.Conversations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

/// <summary>
/// 写入操作之前的检查：宿主与草稿对话允许，extension 只能写入自己创建的对话与开放对话。
/// The check before a write: the host and draft conversations are allowed, and an extension may write only its own and open conversations.
/// </summary>
public sealed class ProjectConversationAccessFacadeTests
{
    [Fact]
    public async Task EnsureWritableAsync_DraftConversation_Allows()
    {
        using var user = Fixture.PushUser("tester");
        await using var fixture = await Fixture.CreateAsync();

        var exception = await Record.ExceptionAsync(() =>
            fixture.Facade.EnsureWritableAsync(Guid.CreateVersion7(), "sample", fixture.Token)
        );

        Assert.Null(exception);
    }

    [Fact]
    public async Task EnsureWritableAsync_OwnConversation_Allows()
    {
        using var user = Fixture.PushUser("tester");
        await using var fixture = await Fixture.CreateAsync();
        var conversationId = await fixture.SeedConversationAsync("sample", generation: 0, withTurn: true);

        var exception = await Record.ExceptionAsync(() =>
            fixture.Facade.EnsureWritableAsync(conversationId, "sample", fixture.Token)
        );

        Assert.Null(exception);
    }

    [Fact]
    public async Task EnsureWritableAsync_RequesterWithoutExtension_Allows()
    {
        using var user = Fixture.PushUser("tester");
        await using var fixture = await Fixture.CreateAsync();
        var conversationId = await fixture.SeedConversationAsync("sample", generation: 1, withTurn: true);

        var exception = await Record.ExceptionAsync(() =>
            fixture.Facade.EnsureWritableAsync(conversationId, requesterExtensionId: null, fixture.Token)
        );

        Assert.Null(exception);
    }

    [Fact]
    public async Task EnsureWritableAsync_OpenConversation_Allows()
    {
        using var user = Fixture.PushUser("tester");
        await using var fixture = await Fixture.CreateAsync();
        var conversationId = await fixture.SeedConversationAsync(
            ConversationCreators.Host,
            generation: 0,
            withTurn: false
        );

        var exception = await Record.ExceptionAsync(() =>
            fixture.Facade.EnsureWritableAsync(conversationId, "sample", fixture.Token)
        );

        Assert.Null(exception);
    }

    [Fact]
    public async Task EnsureWritableAsync_HostConversationWithAcceptedTurn_ThrowsConversationReadOnly()
    {
        using var user = Fixture.PushUser("tester");
        await using var fixture = await Fixture.CreateAsync();
        var conversationId = await fixture.SeedConversationAsync(
            ConversationCreators.Host,
            generation: 0,
            withTurn: true
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Facade.EnsureWritableAsync(conversationId, "sample", fixture.Token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
    }

    [Fact]
    public async Task EnsureWritableAsync_ClearedHostConversation_ThrowsConversationReadOnly()
    {
        using var user = Fixture.PushUser("tester");
        await using var fixture = await Fixture.CreateAsync();
        var conversationId = await fixture.SeedConversationAsync(
            ConversationCreators.Host,
            generation: 1,
            withTurn: false
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Facade.EnsureWritableAsync(conversationId, "sample", fixture.Token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
    }

    [Fact]
    public async Task EnsureWritableAsync_OtherExtensionConversation_ThrowsConversationReadOnly()
    {
        using var user = Fixture.PushUser("tester");
        await using var fixture = await Fixture.CreateAsync();
        var conversationId = await fixture.SeedConversationAsync("other", generation: 0, withTurn: false);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Facade.EnsureWritableAsync(conversationId, "sample", fixture.Token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
    }

    [Fact]
    public async Task EnsureWritableAsync_ConversationOfAnotherUser_AllowsLikeDraft()
    {
        using var user = Fixture.PushUser("tester");
        await using var fixture = await Fixture.CreateAsync();
        var conversationId = await fixture.SeedConversationAsync(
            ConversationCreators.Host,
            generation: 0,
            withTurn: true,
            owner: "other-user"
        );

        var exception = await Record.ExceptionAsync(() =>
            fixture.Facade.EnsureWritableAsync(conversationId, "sample", fixture.Token)
        );

        Assert.Null(exception);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;
        private readonly AgwDbContext _dbContext;

        private Fixture(SqliteConnection connection, DbContextOptions<AgwDbContext> options)
        {
            _connection = connection;
            _options = options;
            _dbContext = new AgwDbContext(options);
            Facade = new ProjectConversationAccessFacade(_dbContext);
        }

        public ProjectConversationAccessFacade Facade { get; }

        public static IDisposable PushUser(string userId) =>
            UserInfoUtil.Push(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"))
            );

        public CancellationToken Token => TestContext.Current.CancellationToken;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using (var setup = new AgwDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            }

            return new Fixture(connection, options);
        }

        public async Task<Guid> SeedConversationAsync(
            string extensionId,
            int generation,
            bool withTurn,
            string owner = "tester"
        )
        {
            var projectId = Guid.CreateVersion7();
            var conversationId = Guid.CreateVersion7();
            using var seedUser = PushUser(owner);
            await using var context = new AgwDbContext(_options);
            context.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "Access Project",
                    Type = ProjectType.UserDefined,
                    CreateBy = owner,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            context.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = conversationId,
                    ProjectId = projectId,
                    ContextId = $"context-{conversationId:N}",
                    Title = "Chat",
                    Generation = generation,
                    ExtensionId = extensionId,
                    CreateBy = owner,
                    CreateTime = TimeProvider.System.GetUtcNow(),
                }
            );
            if (withTurn)
            {
                context.ProjectConversationTurns.Add(
                    new ProjectConversationTurn
                    {
                        Id = Guid.CreateVersion7(),
                        ProjectConversationId = conversationId,
                        TargetId = Guid.CreateVersion7(),
                        Status = ProjectConversationTurnStatus.Completed,
                        InputMessageId = Guid.CreateVersion7(),
                        StartedAt = TimeProvider.System.GetUtcNow(),
                        FinishedAt = TimeProvider.System.GetUtcNow(),
                    }
                );
            }

            await context.SaveChangesAsync(Token);
            return conversationId;
        }

        public async ValueTask DisposeAsync()
        {
            await _dbContext.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
