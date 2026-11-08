using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Sdk.Database;
using Agw.Settings.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Extensions.Database.Tests;

public sealed class ExtensionEnablementStoreTests
{
    [Fact]
    public async Task SetAsync_MissingAndUnchangedValuesKeepTheCurrentVersion()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await ExtensionDatabaseMigrationTests.MigrationTestDatabase.CreateAsync(
            ExtensionDatabaseProvider.Sqlite,
            null
        );
        await using var host = await ExtensionDatabaseTestHost.CreateAsync(database, initiallyEnabled: false);
        await using var scope = host.Services.CreateAsyncScope();
        IExtensionEnablementStore store = scope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();

        Assert.Equal(
            new ExtensionEnablementState(false, 0),
            await store.GetAsync(ExtensionDatabaseTestHost.ExtensionId, cancellationToken)
        );
        Assert.Equal(
            new ExtensionEnablementState(false, 0),
            await store.SetAsync(ExtensionDatabaseTestHost.ExtensionId, false, cancellationToken)
        );
        Assert.Equal(
            new ExtensionEnablementState(true, 1),
            await store.SetAsync(ExtensionDatabaseTestHost.ExtensionId, true, cancellationToken)
        );
        Assert.Equal(
            new ExtensionEnablementState(true, 1),
            await store.SetAsync(ExtensionDatabaseTestHost.ExtensionId, true, cancellationToken)
        );
        Assert.Equal(
            new ExtensionEnablementState(false, 2),
            await store.SetAsync(ExtensionDatabaseTestHost.ExtensionId, false, cancellationToken)
        );
        Assert.Equal(
            new ExtensionEnablementState(false, 2),
            await store.GetAsync(ExtensionDatabaseTestHost.ExtensionId, cancellationToken)
        );
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task TryUpdateGlobalAsync_ConcurrentHostsOnlyOneExpectedVersionWins()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await ExtensionDatabaseMigrationTests.MigrationTestDatabase.CreateAsync(
            ExtensionDatabaseProvider.PostgreSql,
            ExtensionDatabaseMigrationTests.RequirePostgresConnectionString()
        );
        await using var firstHost = await ExtensionDatabaseTestHost.CreateAsync(database, initiallyEnabled: false);
        await using var secondHost = await ExtensionDatabaseTestHost.CreateAsync(database, initiallyEnabled: false);
        await using var firstScope = firstHost.Services.CreateAsyncScope();
        await using var secondScope = secondHost.Services.CreateAsyncScope();
        IExtensionEnablementStore firstEnablement =
            firstScope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
        IExtensionEnablementStore secondEnablement =
            secondScope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
        ISettingsStore firstSettings = firstScope.ServiceProvider.GetRequiredService<ISettingsStore>();
        ISettingsStore secondSettings = secondScope.ServiceProvider.GetRequiredService<ISettingsStore>();
        Guid extensionId = ExtensionDatabaseTestHost.ExtensionId;
        string key = $"extensions.enabled.{extensionId:D}";

        Assert.Equal(
            new ExtensionEnablementState(true, 1),
            await firstEnablement.SetAsync(extensionId, true, cancellationToken)
        );
        SettingSnapshot<TestEnablementSetting> current = Assert.IsType<SettingSnapshot<TestEnablementSetting>>(
            await firstSettings.GetGlobalAsync<TestEnablementSetting>(key, cancellationToken)
        );

        bool[] updated = await Task.WhenAll(
            firstSettings.TryUpdateGlobalAsync(
                key,
                new TestEnablementSetting { Enabled = false },
                current.Version,
                cancellationToken
            ),
            secondSettings.TryUpdateGlobalAsync(
                key,
                new TestEnablementSetting { Enabled = true },
                current.Version,
                cancellationToken
            )
        );

        Assert.Equal(1, updated.Count(result => result));
        Assert.Equal(1, updated.Count(result => !result));
        ExtensionEnablementState sharedState = await firstEnablement.GetAsync(extensionId, cancellationToken);
        Assert.Equal(2, sharedState.Version);
        Assert.Equal(updated[1], sharedState.Enabled);

        ExtensionEnablementState[] unchanged = await Task.WhenAll(
            firstEnablement.SetAsync(extensionId, sharedState.Enabled, cancellationToken),
            secondEnablement.SetAsync(extensionId, sharedState.Enabled, cancellationToken)
        );
        Assert.All(unchanged, state => Assert.Equal(sharedState, state));
        Assert.Equal(sharedState, await secondEnablement.GetAsync(extensionId, cancellationToken));
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task SetAsync_ConcurrentHostsTargetingSameValue_ReturnsCurrentVersion()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await ExtensionDatabaseMigrationTests.MigrationTestDatabase.CreateAsync(
            ExtensionDatabaseProvider.PostgreSql,
            ExtensionDatabaseMigrationTests.RequirePostgresConnectionString()
        );
        await using var firstHost = await ExtensionDatabaseTestHost.CreateAsync(database, initiallyEnabled: false);
        await using var secondHost = await ExtensionDatabaseTestHost.CreateAsync(database, initiallyEnabled: false);
        await using var firstScope = firstHost.Services.CreateAsyncScope();
        await using var secondScope = secondHost.Services.CreateAsyncScope();
        IExtensionEnablementStore first = firstScope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
        IExtensionEnablementStore second = secondScope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
        Guid extensionId = ExtensionDatabaseTestHost.ExtensionId;

        Assert.Equal(new ExtensionEnablementState(true, 1), await first.SetAsync(extensionId, true, cancellationToken));

        ExtensionEnablementState[] disabled = await Task.WhenAll(
            first.SetAsync(extensionId, false, cancellationToken),
            second.SetAsync(extensionId, false, cancellationToken)
        );

        Assert.All(disabled, state => Assert.Equal(new ExtensionEnablementState(false, 2), state));

        ExtensionEnablementState[] enabled = await Task.WhenAll(
            first.SetAsync(extensionId, true, cancellationToken),
            second.SetAsync(extensionId, true, cancellationToken)
        );

        Assert.All(enabled, state => Assert.Equal(new ExtensionEnablementState(true, 3), state));
        Assert.Equal(new ExtensionEnablementState(true, 3), await second.GetAsync(extensionId, cancellationToken));
    }

    private sealed class TestEnablementSetting
    {
        public TestEnablementSetting() { }

        public bool Enabled { get; init; }
    }
}
