using System.Security.Claims;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Domain.Behaviors;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Conversations;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agw.Infrastructure.Tests;

/// <summary>
/// 在对话行锁内记录开放对话的创建方：锁内重新读取创建方、generation 与 Turn，后来的一方读到已确定的创建方而被拒绝。
/// Records the creator of an open conversation under the conversation row lock: the creator, generation and turns are re-read under the lock, so a later claimant sees the decided creator and is refused.
/// </summary>
public sealed class ConversationCreatorStoreTests
{
    private const string Owner = "tester";

    public static bool PostgresEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING"));

    [Fact]
    public async Task ClaimOpenConversationAsync_OpenConversation_RecordsCreator()
    {
        var token = TestContext.Current.CancellationToken;
        using var owner = PushOwner();
        await using var database = await SqliteDatabase.CreateAsync(token);
        var conversationId = await database.SeedConversationAsync(ConversationCreators.Host, generation: 0, token);

        await using (var context = database.CreateContext())
        {
            await new ConversationCreatorStore(context).ClaimOpenConversationAsync(
                conversationId,
                0,
                Claim("sample"),
                token
            );
        }

        Assert.Equal("sample", await database.ReadCreatorAsync(conversationId, token));
    }

    [Fact]
    public async Task ClaimOpenConversationAsync_SameExtensionAgain_KeepsCreator()
    {
        var token = TestContext.Current.CancellationToken;
        using var owner = PushOwner();
        await using var database = await SqliteDatabase.CreateAsync(token);
        var conversationId = await database.SeedConversationAsync("sample", generation: 0, token);
        await database.AddTurnAsync(conversationId, token);

        await using (var context = database.CreateContext())
        {
            await new ConversationCreatorStore(context).ClaimOpenConversationAsync(
                conversationId,
                0,
                Claim("sample"),
                token
            );
        }

        Assert.Equal("sample", await database.ReadCreatorAsync(conversationId, token));
    }

    [Fact]
    public async Task ClaimOpenConversationAsync_ClaimedByOtherExtension_ThrowsConversationReadOnly()
    {
        var token = TestContext.Current.CancellationToken;
        using var owner = PushOwner();
        await using var database = await SqliteDatabase.CreateAsync(token);
        var conversationId = await database.SeedConversationAsync("other", generation: 0, token);

        await using var context = database.CreateContext();
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            new ConversationCreatorStore(context).ClaimOpenConversationAsync(conversationId, 0, Claim("sample"), token)
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Equal("other", await database.ReadCreatorAsync(conversationId, token));
    }

    [Fact]
    public async Task ClaimOpenConversationAsync_OtherExtensionAcceptedFirst_ThrowsConversationReadOnlyAndKeepsCreator()
    {
        var token = TestContext.Current.CancellationToken;
        using var owner = PushOwner();
        await using var database = await SqliteDatabase.CreateAsync(token);
        var conversationId = await database.SeedConversationAsync(ConversationCreators.Host, generation: 0, token);
        await using var later = database.CreateContext();
        // B 先读到开放对话与它的 generation。B first reads the open conversation and its generation.
        var observed = await later
            .ProjectConversations.AsNoTracking()
            .SingleAsync(conversation => conversation.Id == conversationId, token);
        Assert.Equal(ConversationCreators.Host, observed.ExtensionId);

        // A 记录创建方并受理 Turn。A records the creator and accepts a turn.
        await using (var first = database.CreateContext())
        {
            await new ConversationCreatorStore(first).ClaimOpenConversationAsync(
                conversationId,
                observed.Generation,
                Claim("first"),
                token
            );
        }
        await database.AddTurnAsync(conversationId, token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            new ConversationCreatorStore(later).ClaimOpenConversationAsync(
                conversationId,
                observed.Generation,
                Claim("second"),
                token
            )
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Equal("first", await database.ReadCreatorAsync(conversationId, token));
    }

    [Fact]
    public async Task ClaimOpenConversationAsync_HostTurnAcceptedAfterOpenRead_ThrowsConversationReadOnly()
    {
        var token = TestContext.Current.CancellationToken;
        using var owner = PushOwner();
        await using var database = await SqliteDatabase.CreateAsync(token);
        var conversationId = await database.SeedConversationAsync(ConversationCreators.Host, generation: 0, token);
        await using var context = database.CreateContext();
        var observed = await context
            .ProjectConversations.AsNoTracking()
            .SingleAsync(conversation => conversation.Id == conversationId, token);
        await database.AddTurnAsync(conversationId, token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            new ConversationCreatorStore(context).ClaimOpenConversationAsync(
                conversationId,
                observed.Generation,
                Claim("sample"),
                token
            )
        );

        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
        Assert.Equal(ConversationCreators.Host, await database.ReadCreatorAsync(conversationId, token));
    }

    [Fact]
    public async Task ClaimOpenConversationAsync_GenerationChanged_ThrowsConversationSessionConflict()
    {
        var token = TestContext.Current.CancellationToken;
        using var owner = PushOwner();
        await using var database = await SqliteDatabase.CreateAsync(token);
        var conversationId = await database.SeedConversationAsync(ConversationCreators.Host, generation: 1, token);

        await using var context = database.CreateContext();
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            new ConversationCreatorStore(context).ClaimOpenConversationAsync(conversationId, 0, Claim("sample"), token)
        );

        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, exception.Code);
        Assert.Equal(ConversationCreators.Host, await database.ReadCreatorAsync(conversationId, token));
    }

    [Fact(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Requires an isolated PostgreSQL test database with CREATE DATABASE permission."
    )]
    public async Task Postgres_ConcurrentClaims_RecordExactlyOneCreator()
    {
        var token = TestContext.Current.CancellationToken;
        var admin = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")
        )
        {
            Pooling = false,
        };
        var database = $"agw_conversation_creator_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync(token);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync(token);
        }
        try
        {
            using var owner = PushOwner();
            var builder = new DbContextOptionsBuilder<AgwDbContext>();
            AgwDbContextOptionsConfigurator.Configure(
                builder,
                DatabaseProvider.Postgres,
                new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database }.ConnectionString
            );
            var options = builder.Options;
            Guid conversationId;
            await using (var setup = new AgwDbContext(options))
            {
                await setup.Database.MigrateAsync(token);
                conversationId = await SeedConversationAsync(setup, ConversationCreators.Host, 0, token);
            }

            await using var firstContext = new AgwDbContext(options);
            await using var secondContext = new AgwDbContext(options);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var first = Task.Run(
                () =>
                    new ConversationCreatorStore(firstContext).ClaimOpenConversationAsync(
                        conversationId,
                        0,
                        claim =>
                        {
                            Claim("first")(claim);
                            entered.Set();
                            release.Wait(token);
                        },
                        token
                    ),
                token
            );
            entered.Wait(token);
            var second = Task.Run(
                () =>
                    new ConversationCreatorStore(secondContext).ClaimOpenConversationAsync(
                        conversationId,
                        0,
                        Claim("second"),
                        token
                    ),
                token
            );
            // A 持有对话行锁时 B 等待。B waits while A holds the conversation row lock.
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            Assert.False(second.IsCompleted);

            release.Set();
            await first;
            var exception = await Assert.ThrowsAsync<AgwException>(() => second);

            Assert.Equal(ErrorCodes.ConversationReadOnly.Code, exception.Code);
            await using var assertContext = new AgwDbContext(options);
            Assert.Equal(
                "first",
                await assertContext
                    .ProjectConversations.AsNoTracking()
                    .Where(conversation => conversation.Id == conversationId)
                    .Select(conversation => conversation.ExtensionId)
                    .SingleAsync(token)
            );
        }
        finally
        {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Application 在锁内执行的写入规则：检查请求方能否写入，开放对话记录创建方。
    /// The write rule Application runs under the lock: checks that the requester may write and records the creator of an open conversation.
    /// </summary>
    private static Action<ConversationCreatorClaim> Claim(string requesterExtensionId) =>
        claim =>
        {
            var behavior = new ProjectConversationBehavior(claim.Conversation);
            behavior.EnsureWritableBy(requesterExtensionId, claim.HasAcceptedTurns);
            behavior.RecordCreatorIfOpen(requesterExtensionId, claim.HasAcceptedTurns);
        };

    private static IDisposable PushOwner() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner)], "test"))
        );

    private static async Task<Guid> SeedConversationAsync(
        AgwDbContext context,
        string extensionId,
        int generation,
        CancellationToken cancellationToken
    )
    {
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        context.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = "Creator Project",
                Type = ProjectType.UserDefined,
                CreateBy = Owner,
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
                CreateBy = Owner,
                CreateTime = TimeProvider.System.GetUtcNow(),
            }
        );
        await context.SaveChangesAsync(cancellationToken);
        return conversationId;
    }

    private sealed class SqliteDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;

        private SqliteDatabase(SqliteConnection connection, DbContextOptions<AgwDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public static async Task<SqliteDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var setup = new AgwDbContext(options);
            await setup.Database.EnsureCreatedAsync(cancellationToken);
            return new SqliteDatabase(connection, options);
        }

        public AgwDbContext CreateContext() => new(_options);

        public async Task<Guid> SeedConversationAsync(
            string extensionId,
            int generation,
            CancellationToken cancellationToken
        )
        {
            await using var context = CreateContext();
            return await ConversationCreatorStoreTests.SeedConversationAsync(
                context,
                extensionId,
                generation,
                cancellationToken
            );
        }

        public async Task AddTurnAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
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
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task<string> ReadCreatorAsync(Guid conversationId, CancellationToken cancellationToken)
        {
            await using var context = CreateContext();
            return await context
                .ProjectConversations.AsNoTracking()
                .Where(conversation => conversation.Id == conversationId)
                .Select(conversation => conversation.ExtensionId)
                .SingleAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
