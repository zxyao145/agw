using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Auth.Security;
using Agw.Infrastructure.Auth;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Projects.Application;
using Agw.Projects.Contracts;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Agw.Auth.Tests;

public sealed class OidcIdentityStoreTests
{
    [Fact]
    public async Task Resolve_NewIdentity_CreatesOwnedDefaultsAndReusesStableId()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Context();
        var store = database.Store(context);
        var first = await store.ResolveAsync(Identity("one"), TestContext.Current.CancellationToken);
        var again = await store.ResolveAsync(
            Identity("one") with
            {
                DisplayName = "Updated",
            },
            TestContext.Current.CancellationToken
        );
        var other = await store.ResolveAsync(Identity("two"), TestContext.Current.CancellationToken);
        var otherIssuer = await store.ResolveAsync(
            Identity("one") with
            {
                Issuer = "https://other.example",
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal("10000", first.UserId);
        Assert.Equal(first.UserId, again.UserId);
        Assert.Equal("Updated", again.DisplayName);
        Assert.Equal("10001", other.UserId);
        Assert.NotEqual(first.UserId, otherIssuer.UserId);
        using (UserInfoUtil.Push(OidcPrincipal.Create(first)))
        {
            var projects = await context.Projects.ToArrayAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, projects.Length);
            Assert.All(
                projects,
                project =>
                {
                    Assert.Equal(first.UserId, project.CreateBy);
                    Assert.Null(project.Workspace);
                }
            );
            Assert.Single(await context.AuthUsers.ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Single(await context.AuthExternalIdentities.ToArrayAsync(TestContext.Current.CancellationToken));
        }
        using (UserInfoUtil.Push(null))
        {
            Assert.Empty(await context.Projects.ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await context.AuthUsers.ToArrayAsync(TestContext.Current.CancellationToken));
        }
        Assert.Null(UserInfoUtil.Current);
    }

    [Fact]
    public async Task Resolve_DefaultProjectsFailAfterSave_RollsBackUserIdentityProjectsAndSequence()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var context = database.Context())
        {
            var failing = new EfOidcIdentityStore(
                context,
                new FailingProjectInitializer(context),
                new EfApiTokenStore(context),
                database.Clock,
                database.Options
            );
            await Assert.ThrowsAsync<AgwException>(() =>
                failing.ResolveAsync(Identity("failed-defaults"), TestContext.Current.CancellationToken)
            );
        }
        Assert.Null(UserInfoUtil.Current);
        // A fresh context ensures EF's tracked rows cannot hide committed leftovers.
        await using var fresh = database.Context();
        Assert.Equal(1, await fresh.AuthUsers.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken));
        Assert.Empty(
            await fresh.AuthExternalIdentities.IgnoreQueryFilters().ToArrayAsync(TestContext.Current.CancellationToken)
        );
        Assert.Empty(await fresh.Projects.IgnoreQueryFilters().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            10000L,
            await fresh.AuthUserIdSequences.Select(row => row.NextId).SingleAsync(TestContext.Current.CancellationToken)
        );
        var retry = await database
            .Store(fresh)
            .ResolveAsync(Identity("failed-defaults"), TestContext.Current.CancellationToken);
        Assert.Equal("10000", retry.UserId);
        using var owner = UserInfoUtil.Push(OidcPrincipal.Create(retry));
        Assert.Equal(2, await fresh.Projects.CountAsync(TestContext.Current.CancellationToken));
    }

    internal sealed class FailingProjectInitializer : IUserProjectInitializer
    {
        private readonly AgwDbContext _context;

        public FailingProjectInitializer(AgwDbContext context)
        {
            _context = context;
        }

        public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
        {
            await new UserProjectInitializer(_context).EnsureDefaultsAsync(cancellationToken);
            Assert.Equal(2, await _context.Projects.CountAsync(cancellationToken));
            throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    [Fact]
    public async Task Resolve_ConcurrentCallbacks_ProvisionOnlyOneUser()
    {
        await using var database = await TestDatabase.CreateAsync();
        var ids = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(async _ =>
                {
                    await using var context = database.Context();
                    return (
                        await database
                            .Store(context)
                            .ResolveAsync(Identity("concurrent"), TestContext.Current.CancellationToken)
                    ).UserId;
                })
        );
        Assert.Single(ids.Distinct());
        await using var verification = database.Context();
        Assert.Equal(
            2,
            await verification
                .AuthUsers.IgnoreQueryFilters([UserScopeQueryFilterNames.UserScope])
                .CountAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            2,
            await verification
                .Projects.IgnoreQueryFilters([UserScopeQueryFilterNames.UserScope])
                .CountAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Exchange_WrongProofThenValidProof_ConsumesOnceAndPreservesOwner()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Context();
        var store = database.Store(context);
        var user = await store.ResolveAsync(Identity("desktop"), TestContext.Current.CancellationToken);
        var verifier = DesktopLoginProof.CreateCode();
        var code = await store.CreateDesktopGrantAsync(
            user,
            "company",
            DesktopLoginProof.Challenge(verifier),
            TestContext.Current.CancellationToken
        );

        var wrong = await Assert.ThrowsAsync<AgwException>(() =>
            store.ExchangeAsync(code, DesktopLoginProof.CreateCode(), TestContext.Current.CancellationToken)
        );
        Assert.Equal(ErrorCodes.DesktopLoginInvalid.Code, wrong.Code);
        var result = await store.ExchangeAsync(code, verifier, TestContext.Current.CancellationToken);
        Assert.Equal(user.UserId, result.UserId);
        Assert.StartsWith("agw_", result.Token);
        await Assert.ThrowsAsync<AgwException>(() =>
            store.ExchangeAsync(code, verifier, TestContext.Current.CancellationToken)
        );
        var token = await new EfApiTokenStore(context).ValidateTokenAsync(
            result.Token,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(user.UserId, token!.UserId);
        Assert.Equal(result.TokenId, token.TokenId);
        Assert.Equal("company", token.LoginProvider);
        Assert.Empty(
            await context
                .AuthDesktopLoginGrants.IgnoreQueryFilters([UserScopeQueryFilterNames.UserScope])
                .ToArrayAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Exchange_ConcurrentReplicas_OnlyOneTokenIsIssued()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var initial = database.Context();
        var store = database.Store(initial);
        var user = await store.ResolveAsync(Identity("race"), TestContext.Current.CancellationToken);
        var verifier = DesktopLoginProof.CreateCode();
        var code = await store.CreateDesktopGrantAsync(
            user,
            "company",
            DesktopLoginProof.Challenge(verifier),
            TestContext.Current.CancellationToken
        );
        var outcomes = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(async _ =>
                {
                    await using var context = database.Context();
                    try
                    {
                        await database
                            .Store(context)
                            .ExchangeAsync(code, verifier, TestContext.Current.CancellationToken);
                        return true;
                    }
                    catch (AgwException)
                    {
                        return false;
                    }
                })
        );
        Assert.Single(outcomes, success => success);
        Assert.Equal(
            1,
            await initial
                .ApiTokens.IgnoreQueryFilters([UserScopeQueryFilterNames.UserScope])
                .CountAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Exchange_TokenCreationFails_RollsBackTokenAndGrantConsumption()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Context();
        var store = database.Store(context);
        var user = await store.ResolveAsync(Identity("rollback"), TestContext.Current.CancellationToken);
        var verifier = DesktopLoginProof.CreateCode();
        var code = await store.CreateDesktopGrantAsync(
            user,
            "company",
            DesktopLoginProof.Challenge(verifier),
            TestContext.Current.CancellationToken
        );
        var failing = database.Store(context, new FailingTokenStore(new EfApiTokenStore(context)));
        await Assert.ThrowsAsync<AgwException>(() =>
            failing.ExchangeAsync(code, verifier, TestContext.Current.CancellationToken)
        );

        await using var fresh = database.Context();
        Assert.Empty(
            await fresh
                .ApiTokens.IgnoreQueryFilters([UserScopeQueryFilterNames.UserScope])
                .ToArrayAsync(TestContext.Current.CancellationToken)
        );
        Assert.Single(
            await fresh
                .AuthDesktopLoginGrants.IgnoreQueryFilters([UserScopeQueryFilterNames.UserScope])
                .ToArrayAsync(TestContext.Current.CancellationToken)
        );
        var result = await database.Store(fresh).ExchangeAsync(code, verifier, TestContext.Current.CancellationToken);
        Assert.Equal(user.UserId, result.UserId);
    }

    [Fact]
    public async Task Exchange_ExpiredOrDisabled_FailsWithoutIssuingToken()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Context();
        var store = database.Store(context);
        var user = await store.ResolveAsync(Identity("expired"), TestContext.Current.CancellationToken);
        var verifier = DesktopLoginProof.CreateCode();
        var code = await store.CreateDesktopGrantAsync(
            user,
            "company",
            DesktopLoginProof.Challenge(verifier),
            TestContext.Current.CancellationToken
        );
        database.Options.Providers["company"].Enabled = false;
        await Assert.ThrowsAsync<AgwException>(() =>
            store.ExchangeAsync(code, verifier, TestContext.Current.CancellationToken)
        );
        database.Options.Providers["company"].Enabled = true;
        database.Clock.Now += TimeSpan.FromMinutes(2);
        await Assert.ThrowsAsync<AgwException>(() =>
            store.ExchangeAsync(code, verifier, TestContext.Current.CancellationToken)
        );
        Assert.Equal(1, await store.DeleteExpiredGrantsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(
            await context
                .ApiTokens.IgnoreQueryFilters([UserScopeQueryFilterNames.UserScope])
                .ToArrayAsync(TestContext.Current.CancellationToken)
        );
    }

    [Theory]
    [InlineData("previous")]
    [InlineData("latest")]
    public async Task Migrations_FreshAndUpgrade_PreserveAdministratorAndStartAt10000(string target)
    {
        await using var database = await TestDatabase.CreateAsync(target);
        await using var context = database.Context();
        var tokens = new EfApiTokenStore(context);
        CreatedApiToken legacy;
        using (UserInfoUtil.Push(OidcPrincipal.Create(new OidcUser("1001", "admin", 1, null))))
            legacy = await tokens.CreateTokenAsync("legacy-client", TestContext.Current.CancellationToken);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            "1001",
            (await tokens.ValidateTokenAsync(legacy.Token, TestContext.Current.CancellationToken))!.UserId
        );
        var user = await database
            .Store(context)
            .ResolveAsync(Identity("after-migration"), TestContext.Current.CancellationToken);
        Assert.Equal("10000", user.UserId);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }

    private static VerifiedOidcIdentity Identity(string subject) =>
        new("company", "https://issuer.example", subject, "Person", "shared@example.com");

    internal sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"agw-oidc-{Guid.NewGuid():N}.db");
        private string? _postgresConnection;
        private string? _postgresAdmin;
        private string? _databaseName;
        public MutableClock Clock { get; } = new();
        public OidcOptions Options { get; } =
            new() { Providers = new() { ["company"] = new OidcProviderOptions { Enabled = true } } };

        public static async Task<TestDatabase> CreateAsync(string? migration = null)
        {
            var database = new TestDatabase();
            database._postgresAdmin = Environment.GetEnvironmentVariable("AGW_TEST_OIDC_POSTGRES");
            if (!string.IsNullOrWhiteSpace(database._postgresAdmin))
            {
                database._databaseName = "agw_oidc_test_" + Guid.NewGuid().ToString("N");
                await using var connection = new NpgsqlConnection(database._postgresAdmin);
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = new NpgsqlCommand("CREATE DATABASE " + database._databaseName, connection);
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                database._postgresConnection = new NpgsqlConnectionStringBuilder(database._postgresAdmin)
                {
                    Database = database._databaseName,
                    Pooling = false,
                }.ConnectionString;
            }
            await using var context = database.Context();
            if (migration == null)
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            else
            {
                var target = migration == "previous" ? context.Database.GetMigrations().SkipLast(1).Last() : null;
                await context.GetService<IMigrator>().MigrateAsync(target, TestContext.Current.CancellationToken);
            }
            return database;
        }

        public AgwDbContext Context()
        {
            var options = new DbContextOptionsBuilder<AgwDbContext>();
            if (_postgresConnection != null)
                options.UseNpgsql(
                    _postgresConnection,
                    provider => provider.MigrationsAssembly("Agw.Migrations.Postgres")
                );
            else
                options.UseSqlite(
                    $"Data Source={_path};Pooling=False;Default Timeout=30",
                    provider => provider.MigrationsAssembly("Agw.Migrations.Sqlite")
                );
            return new AgwDbContext(
                options
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(
                        new EntityCreatorInterceptor(new AuditUser(), Clock),
                        new EntityModifierInterceptor(new AuditUser(), Clock)
                    )
                    .Options
            );
        }

        public EfOidcIdentityStore Store(AgwDbContext context, IApiTokenStore? tokens = null) =>
            new(context, new UserProjectInitializer(context), tokens ?? new EfApiTokenStore(context), Clock, Options);

        public async ValueTask DisposeAsync()
        {
            if (_postgresAdmin != null && _databaseName != null)
            {
                await using var connection = new NpgsqlConnection(_postgresAdmin);
                await connection.OpenAsync(CancellationToken.None);
                await using var command = new NpgsqlCommand(
                    "DROP DATABASE " + _databaseName + " WITH (FORCE)",
                    connection
                );
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            File.Delete(_path);
            File.Delete(_path + "-wal");
            File.Delete(_path + "-shm");
        }
    }

    internal sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AuditUser : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.RequiredUserId;
    }

    private sealed class FailingTokenStore : IApiTokenStore
    {
        private readonly IApiTokenStore _inner;

        public FailingTokenStore(IApiTokenStore inner)
        {
            _inner = inner;
        }

        public Task<IReadOnlyList<ApiTokenSummary>> ListTokensAsync(CancellationToken cancellationToken = default) =>
            _inner.ListTokensAsync(cancellationToken);

        public Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.RevokeTokenAsync(id, cancellationToken);

        public Task<ApiTokenIdentity?> ValidateTokenAsync(
            string token,
            CancellationToken cancellationToken = default
        ) => _inner.ValidateTokenAsync(token, cancellationToken);

        public async Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default)
        {
            await _inner.CreateTokenAsync(name, cancellationToken);
            throw new AgwException(ErrorCodes.InvalidParam);
        }
    }
}
