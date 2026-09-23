using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Infrastructure.Auth;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Settings;
using Agw.Settings;
using Agw.Settings.Application.Persistence;
using Agw.Settings.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Setup.Tests;

public sealed class DatabaseInitializationStateStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agw-auth-state-{Guid.NewGuid():N}");
    private ServiceProvider _services = null!;
    private string ConnectionString => $"Data Source={Path.Combine(_directory, "state.db")};Pooling=False";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddAuth();
        services.AddSettings();
        services.AddScoped<ISettingsPersistence, EfSettingsPersistence>();
        services.AddScoped<ISettingsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddDbContext<AgwDbContext>(options => options.UseSqlite(ConnectionString));
        services.AddScoped<IServerAuthStatePersistence, SettingsServerAuthStatePersistence>();
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<AgwDbContext>()
            .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PersistAsync_CompletedSetup_IsVisibleToAnotherReplicaWithoutStateFile()
    {
        var writer = CreateStore();
        var reader = CreateStore();
        await reader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(reader.IsInitialized);

        await writer.PersistAsync("password-hash", TestContext.Current.CancellationToken);
        await reader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(reader.IsInitialized);
        Assert.Equal(new AuthenticationSnapshot("password-hash", 1), reader.GetAuthenticationSnapshot());
        Assert.False(File.Exists(Path.Combine(_directory, "server-state.json")));
    }

    [Fact]
    public async Task PersistAsync_ConcurrentSetup_DoesNotOverwriteWinner()
    {
        var first = CreateStore();
        var second = CreateStore();
        await Task.WhenAll(
            first.PersistAsync("first-hash", TestContext.Current.CancellationToken),
            second.PersistAsync("second-hash", TestContext.Current.CancellationToken)
        );
        await first.RefreshAsync(TestContext.Current.CancellationToken);
        await second.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.GetAuthenticationSnapshot(), second.GetAuthenticationSnapshot());
        Assert.Equal(1, first.GetAuthenticationSnapshot().SessionVersion);
        var winner = first.GetAuthenticationSnapshot().PasswordHash;
        await first.PersistAsync("replacement-hash", TestContext.Current.CancellationToken);
        Assert.Equal(winner, first.GetAuthenticationSnapshot().PasswordHash);
    }

    [Fact]
    public async Task UpdatePasswordAsync_AnotherReplica_RefreshesPasswordAndSessionVersion()
    {
        var writer = CreateStore();
        var reader = CreateStore();
        await writer.PersistAsync("old-hash", TestContext.Current.CancellationToken);
        await reader.RefreshAsync(TestContext.Current.CancellationToken);
        await writer.UpdatePasswordAsync("new-hash", TestContext.Current.CancellationToken);
        await reader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new AuthenticationSnapshot("new-hash", 2), reader.GetAuthenticationSnapshot());
    }

    [Fact]
    public async Task UpdatePasswordAsync_BeforeSetup_DoesNotInitializeServer()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<AgwException>(() =>
            store.UpdatePasswordAsync("hash", TestContext.Current.CancellationToken)
        );
        Assert.False(store.IsInitialized);
    }

    [Fact]
    public async Task RefreshAsync_MissingSchema_ReturnsUninitializedWithoutCreatingSchema()
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await context.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        var store = CreateStore();
        await store.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(store.IsInitialized);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'";
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshAsync_DatabaseReadFails_DiscardsCachedCredentials()
    {
        var store = CreateStore();
        await store.PersistAsync("hash", TestContext.Current.CancellationToken);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE setting RENAME COLUMN ValueJson TO BrokenColumn",
            TestContext.Current.CancellationToken
        );

        await Assert.ThrowsAsync<SqliteException>(() => store.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(store.IsInitialized);
        Assert.Null(store.GetAuthenticationSnapshot().PasswordHash);
        Assert.Equal(0, store.GetAuthenticationSnapshot().SessionVersion);
    }

    private DatabaseInitializationStateStore CreateStore() => new(_services.GetRequiredService<IServiceScopeFactory>());

    [Theory]
    [InlineData("{broken-private-marker")]
    [InlineData("{}")]
    [InlineData(
        "{\"passwordHash\":\"private-marker\",\"sessionVersion\":0,\"initializedAt\":\"2026-09-12T00:00:00Z\"}"
    )]
    public async Task RefreshAsync_InvalidAuthGroup_FailsClosedAndCannotBeOverwrittenBySetup(string json)
    {
        var store = CreateStore();
        await store.PersistAsync("original-hash", TestContext.Current.CancellationToken);
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var row = await context.Settings.IgnoreQueryFilters().SingleAsync(TestContext.Current.CancellationToken);
        row.ValueJson = json;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            store.RefreshAsync(TestContext.Current.CancellationToken)
        );
        Assert.DoesNotContain("private-marker", error.ToString());
        Assert.False(store.IsInitialized);
        await Assert.ThrowsAsync<AgwException>(() =>
            store.PersistAsync("replacement-hash", TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            json,
            (
                await context
                    .Settings.AsNoTracking()
                    .IgnoreQueryFilters()
                    .SingleAsync(TestContext.Current.CancellationToken)
            ).ValueJson
        );
    }

    [Fact]
    public async Task ReadAsync_OnlyUserAuthGroupExists_DoesNotInitializeServer()
    {
        using var user = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "test"))
        );
        await using var scope = _services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<ISettingsStore>()
            .TryCreateCurrentUserAsync(
                "auth",
                new
                {
                    PasswordHash = "user-hash",
                    SessionVersion = 1,
                    InitializedAt = DateTimeOffset.Parse("2026-09-12T00:00:00Z"),
                },
                TestContext.Current.CancellationToken
            );
        var store = CreateStore();
        await store.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(store.IsInitialized);
        Assert.Null(store.GetAuthenticationSnapshot().PasswordHash);
    }

    [Fact]
    public async Task AddSetup_ReadOnlyReplica_ReadsSharedStateWithoutRegisteringWriters()
    {
        await CreateStore().PersistAsync("hash", TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuth();
        services.AddSettings();
        services.AddScoped<ISettingsPersistence, EfSettingsPersistence>();
        services.AddScoped<ISettingsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddDbContext<AgwDbContext>(options => options.UseSqlite(ConnectionString));
        services.AddScoped<IServerAuthStatePersistence, SettingsServerAuthStatePersistence>();
        services.AddSetup(new ConfigurationBuilder().Build(), readOnly: true);
        await using var replica = services.BuildServiceProvider();
        await replica
            .GetRequiredService<DatabaseInitializationStateStore>()
            .RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(replica.GetRequiredService<IServerInitializationState>().IsInitialized);
        Assert.Equal(
            "hash",
            replica.GetRequiredService<IAuthenticationStateReader>().GetAuthenticationSnapshot().PasswordHash
        );
        Assert.Null(replica.GetService<IAuthenticationStateStore>());
        Assert.Null(replica.GetService<IInitializationStateStore>());
    }

    [Fact]
    public async Task UpdatePasswordAsync_ConcurrentChanges_RejectsLostUpdate()
    {
        await CreateStore().PersistAsync("initial-hash", TestContext.Current.CancellationToken);
        await using var first = _services.CreateAsyncScope();
        await using var second = _services.CreateAsyncScope();
        var secondContext = second.ServiceProvider.GetRequiredService<AgwDbContext>();
        // Track the old version before another replica commits a password change.
        await secondContext.Settings.IgnoreQueryFilters().SingleAsync(TestContext.Current.CancellationToken);
        await first
            .ServiceProvider.GetRequiredService<IServerAuthStatePersistence>()
            .UpdatePasswordAsync("first-change", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<AgwException>(() =>
            second
                .ServiceProvider.GetRequiredService<IServerAuthStatePersistence>()
                .UpdatePasswordAsync("stale-change", TestContext.Current.CancellationToken)
        );
        var reader = CreateStore();
        await reader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new AuthenticationSnapshot("first-change", 2), reader.GetAuthenticationSnapshot());
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }
}
