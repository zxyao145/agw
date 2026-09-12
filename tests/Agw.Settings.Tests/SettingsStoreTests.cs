using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Settings;
using Agw.Settings.Application.Persistence;
using Agw.Settings.Contracts;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Settings;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Settings.Tests;

public sealed class SettingsStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"agw-settings-{Guid.NewGuid():N}");
    private readonly TestClock _clock = new();
    private ServiceProvider _services = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuth();
        services.AddSettings();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddScoped<IEntityAuditUserIdProvider, AuditActor>();
        services.AddScoped<EntityCreatorInterceptor>();
        services.AddScoped<EntityModifierInterceptor>();
        services.AddScoped<ISettingsPersistence, EfSettingsPersistence>();
        services.AddDbContext<AgwDbContext>(
            (provider, options) =>
                options
                    .UseSqlite($"Data Source={Path.Combine(_directory, "settings.db")};Pooling=False")
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(
                        provider.GetRequiredService<EntityCreatorInterceptor>(),
                        provider.GetRequiredService<EntityModifierInterceptor>()
                    )
        );
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AgwDbContext>().Database.EnsureCreatedAsync(Ct);
    }

    [Fact]
    public async Task GlobalWrite_DifferentActors_PreservesCreationAuditAndRecordsModifier()
    {
        var createdAt = _clock.Now;
        using (UserInfoUtil.Push(Principal("creator")))
        {
            await using var scope = _services.CreateAsyncScope();
            Assert.True(await Store(scope).TryCreateGlobalAsync("display", new Preferences("light"), Ct));
        }
        _clock.Now = _clock.Now.AddMinutes(5);
        using (UserInfoUtil.Push(Principal("editor")))
        {
            await using var scope = _services.CreateAsyncScope();
            Assert.True(await Store(scope).TryUpdateGlobalAsync("display", new Preferences("dark"), 1, Ct));
        }
        var row = await ReadRowAsync("display");
        Assert.Null(row.UserId);
        Assert.Equal("creator", row.CreateBy);
        Assert.Equal(createdAt, row.CreateTime);
        Assert.Equal("editor", row.UpdateBy);
        Assert.Equal(_clock.Now, row.UpdateTime);
        Assert.Equal(2, row.Version);
    }

    [Fact]
    public async Task UserGroups_DifferentUsers_DoNotMergeOrReadGlobalDefaults()
    {
        await using var scope = _services.CreateAsyncScope();
        var store = Store(scope);
        Assert.True(await store.TryCreateGlobalAsync("display", new Preferences("global"), Ct));
        using (UserInfoUtil.Push(Principal("alice")))
        {
            Assert.Null(await store.GetCurrentUserAsync<Preferences>("display", Ct));
            Assert.True(await store.TryCreateCurrentUserAsync("display", new Preferences("alice"), Ct));
            Assert.Equal("alice", (await store.GetCurrentUserAsync<Preferences>("display", Ct))!.Value.Theme);
            Assert.Equal("global", (await store.GetGlobalAsync<Preferences>("display", Ct))!.Value.Theme);
        }
        using (UserInfoUtil.Push(Principal("bob")))
        {
            Assert.Null(await store.GetCurrentUserAsync<Preferences>("display", Ct));
            Assert.False(await store.TryUpdateCurrentUserAsync("display", new Preferences("bob"), 1, Ct));
            Assert.True(await store.TryCreateCurrentUserAsync("display", new Preferences("bob"), Ct));
            Assert.Equal("bob", (await store.GetCurrentUserAsync<Preferences>("display", Ct))!.Value.Theme);
        }
        var alice = await ReadRowAsync("display", "alice");
        Assert.Equal("alice", alice.CreateBy);
        Assert.Null(alice.UpdateTime);
    }

    [Fact]
    public async Task UserOperations_WithoutIdentity_RejectAllAccess()
    {
        using var user = UserInfoUtil.Push(null);
        await using var scope = _services.CreateAsyncScope();
        var store = Store(scope);
        await Assert.ThrowsAsync<AgwException>(() => store.GetCurrentUserAsync<Preferences>("display", Ct));
        await Assert.ThrowsAsync<AgwException>(() =>
            store.TryCreateCurrentUserAsync("display", new Preferences("x"), Ct)
        );
        await Assert.ThrowsAsync<AgwException>(() =>
            store.TryUpdateCurrentUserAsync("display", new Preferences("x"), 1, Ct)
        );
    }

    [Fact]
    public async Task TryCreateGlobal_ConcurrentWriters_OnlyOneWins()
    {
        await using var first = _services.CreateAsyncScope();
        await using var second = _services.CreateAsyncScope();
        var results = await Task.WhenAll(
            Store(first).TryCreateGlobalAsync("display", new Preferences("a"), Ct),
            Store(second).TryCreateGlobalAsync("display", new Preferences("b"), Ct)
        );
        Assert.Single(results, won => won);
        Assert.Equal(1, (await ReadRowAsync("display")).Version);
    }

    [Fact]
    public async Task UniqueIndexes_EnforceGlobalAndPerUserKeys()
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        context.Settings.Add(
            new Setting
            {
                Key = "same",
                ValueJson = "{}",
                Version = 1,
            }
        );
        await context.SaveChangesAsync(Ct);
        context.ChangeTracker.Clear();
        context.Settings.Add(
            new Setting
            {
                Key = "same",
                ValueJson = "{}",
                Version = 1,
            }
        );
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        context.ChangeTracker.Clear();
        using var user = UserInfoUtil.Push(Principal("alice"));
        context.Settings.Add(
            new Setting
            {
                Key = "same",
                UserId = "alice",
                ValueJson = "{}",
                Version = 1,
            }
        );
        await context.SaveChangesAsync(Ct);
        context.ChangeTracker.Clear();
        context.Settings.Add(
            new Setting
            {
                Key = "same",
                UserId = "alice",
                ValueJson = "{}",
                Version = 1,
            }
        );
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Update_StaleTrackedVersion_DoesNotChangeWinnerOrAudit()
    {
        await using var first = _services.CreateAsyncScope();
        await using var second = _services.CreateAsyncScope();
        await Store(first).TryCreateGlobalAsync("display", new Preferences("original"), Ct);
        await second.ServiceProvider.GetRequiredService<AgwDbContext>().Settings.IgnoreQueryFilters().SingleAsync(Ct);
        _clock.Now = _clock.Now.AddMinutes(1);
        using (UserInfoUtil.Push(Principal("winner")))
            Assert.True(await Store(first).TryUpdateGlobalAsync("display", new Preferences("winner"), 1, Ct));
        var before = await ReadRowAsync("display");
        _clock.Now = _clock.Now.AddMinutes(1);
        using (UserInfoUtil.Push(Principal("loser")))
            Assert.False(await Store(second).TryUpdateGlobalAsync("display", new Preferences("loser"), 1, Ct));
        var after = await ReadRowAsync("display");
        Assert.Equal(before.ValueJson, after.ValueJson);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.UpdateBy, after.UpdateBy);
        Assert.Equal(before.UpdateTime, after.UpdateTime);
    }

    [Theory]
    [InlineData("CreateBy")]
    [InlineData("CreateTime")]
    [InlineData("UserId")]
    [InlineData("Key")]
    public async Task Update_ImmutableFields_AreRejected(string property)
    {
        await using var scope = _services.CreateAsyncScope();
        await Store(scope).TryCreateGlobalAsync("display", new Preferences("x"), Ct);
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var row = await context.Settings.IgnoreQueryFilters().SingleAsync(Ct);
        var entry = context.Entry(row).Property(property);
        entry.CurrentValue = property == "CreateTime" ? _clock.Now.AddHours(1) : "changed";
        await Assert.ThrowsAsync<AgwException>(() => context.SaveChangesAsync(Ct));
        Assert.Equal("1001", (await ReadRowAsync("display")).CreateBy);
    }

    [Theory]
    [InlineData("{broken-secret-marker")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task Read_InvalidJson_RejectsWithoutExposingValue(string json)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        context.Settings.Add(
            new Setting
            {
                Key = "bad",
                ValueJson = json,
                Version = 1,
            }
        );
        await context.SaveChangesAsync(Ct);
        var error = await Assert.ThrowsAsync<AgwException>(() => Store(scope).GetGlobalAsync<Preferences>("bad", Ct));
        Assert.DoesNotContain("secret-marker", error.ToString());
    }

    private async Task<Setting> ReadRowAsync(string key, string? userId = null)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<AgwDbContext>()
            .Settings.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(row => row.Key == key && row.UserId == userId, Ct);
    }

    private static ISettingsStore Store(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ISettingsStore>();

    private static ClaimsPrincipal Principal(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    public sealed record Preferences(string Theme);

    private sealed class AuditActor : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.UserId ?? "1001";
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-12T00:00:00Z");

        public override DateTimeOffset GetUtcNow() => Now;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }
}
