using Agw.Infrastructure.Auth;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace Agw.Auth.Tests;

public sealed class EfApiTokenStoreTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 12, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateTokenAsync_PersistsHashCreatorAndUtcCreationTime()
    {
        await using var fixture = await TokenStoreFixture.CreateAsync();

        var created = await fixture.Store.CreateTokenAsync(
            "Desktop",
            TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        var persisted = await fixture.Context.ApiTokens
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("agw_", created.Token);
        Assert.Equal("Desktop", persisted.Name);
        Assert.Equal("DESKTOP", persisted.NormalizedName);
        Assert.Equal("creator-42", persisted.CreateBy);
        Assert.Equal(CreatedAt, persisted.CreateTime);
        Assert.Equal(CreatedAt, created.CreatedAt);
        Assert.DoesNotContain(created.Token, persisted.SecretHash, StringComparison.Ordinal);
        Assert.Equal(64, persisted.SecretHash.Length);
        Assert.True(await fixture.Store.ValidateTokenAsync(
            created.Token,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateTokenAsync_WhenNameOnlyDiffersByCase_ThrowsConflict()
    {
        await using var fixture = await TokenStoreFixture.CreateAsync();
        await fixture.Store.CreateTokenAsync(
            "Desktop",
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Store.CreateTokenAsync(
                " desktop ",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ApiTokenNameAlreadyExists.Code, exception.Code);
    }

    [Fact]
    public async Task RevokeTokenAsync_WhenTokenExists_DeletesAndInvalidatesIt()
    {
        await using var fixture = await TokenStoreFixture.CreateAsync();
        var created = await fixture.Store.CreateTokenAsync(
            "CLI",
            TestContext.Current.CancellationToken);

        var revoked = await fixture.Store.RevokeTokenAsync(
            created.Id,
            TestContext.Current.CancellationToken);

        Assert.True(revoked);
        Assert.False(await fixture.Store.ValidateTokenAsync(
            created.Token,
            TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Store.ListTokensAsync(
            TestContext.Current.CancellationToken));
    }

    private sealed class TokenStoreFixture : IAsyncDisposable
    {
        private TokenStoreFixture(
            SqliteConnection connection,
            AgwDbContext context)
        {
            Connection = connection;
            Context = context;
            Store = new EfApiTokenStore(context);
        }

        public SqliteConnection Connection { get; }
        public AgwDbContext Context { get; }
        public EfApiTokenStore Store { get; }

        public static async Task<TokenStoreFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new EntityCreatorInterceptor(
                    new TestUserIdProvider("creator-42"),
                    new FixedTimeProvider(CreatedAt)))
                .Options;
            var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new TokenStoreFixture(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestUserIdProvider : IEntityAuditUserIdProvider
    {
        private readonly string _userId;

        public TestUserIdProvider(string userId)
        {
            _userId = userId;
        }

        public string GetUserId() => _userId;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
