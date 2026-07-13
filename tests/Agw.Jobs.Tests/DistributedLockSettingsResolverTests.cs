using Agw.Infrastructure.Configuration;
using Agw.Shared.Configuration;

namespace Agw.Jobs.Tests;

public class DistributedLockSettingsResolverTests
{
    [Theory]
    [InlineData(DatabaseProvider.Sqlite, DistributedLockProvider.InMemory)]
    [InlineData(DatabaseProvider.Postgres, DistributedLockProvider.Postgres)]
    public void Resolve_WhenProviderIsMissing_InfersProviderFromDatabase(
        DatabaseProvider databaseProvider,
        DistributedLockProvider expectedProvider)
    {
        var settings = new DistributedLockSettings();

        var result = DistributedLockSettingsResolver.Resolve(
            settings,
            databaseProvider,
            "Host=database");

        Assert.Equal(expectedProvider, result.Provider);
    }

    [Theory]
    [InlineData(DistributedLockProvider.InMemory)]
    [InlineData(DistributedLockProvider.Postgres)]
    public void Resolve_WhenProviderIsExplicit_UsesProvider(DistributedLockProvider provider)
    {
        var result = DistributedLockSettingsResolver.Resolve(
            new DistributedLockSettings { Provider = provider },
            DatabaseProvider.Sqlite,
            "Host=database");

        Assert.Equal(provider, result.Provider);
    }

    [Fact]
    public void Resolve_WhenPostgresConnectionStringIsBlank_ReusesDatabaseConnectionString()
    {
        var result = DistributedLockSettingsResolver.Resolve(
            new DistributedLockSettings { Provider = DistributedLockProvider.Postgres, ConnectionString = " " },
            DatabaseProvider.Sqlite,
            "Host=database");

        Assert.Equal("Host=database", result.ConnectionString);
    }

    [Fact]
    public void Resolve_WhenPostgresConnectionStringIsConfigured_UsesConfiguredConnectionString()
    {
        var result = DistributedLockSettingsResolver.Resolve(
            new DistributedLockSettings { Provider = DistributedLockProvider.Postgres, ConnectionString = "Host=locks" },
            DatabaseProvider.Postgres,
            "Host=database");

        Assert.Equal("Host=locks", result.ConnectionString);
    }

    [Fact]
    public void Resolve_WhenProviderIsInMemory_ClearsConnectionString()
    {
        var result = DistributedLockSettingsResolver.Resolve(
            new DistributedLockSettings { Provider = DistributedLockProvider.InMemory, ConnectionString = "unused" },
            DatabaseProvider.Postgres,
            "Host=database");

        Assert.Equal(string.Empty, result.ConnectionString);
    }
}
