using Agw.Setup.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace Agw.Setup.Tests;

public sealed class ConfiguredSetupBootstrapTests
{
    [Fact]
    public void FromConfiguration_WhenSetupSectionIsMissing_ReturnsNotConfigured()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>());

        var bootstrap = ConfiguredSetupBootstrap.FromConfiguration(
            configuration,
            CreatePaths());

        Assert.False(bootstrap.IsConfigured);
        Assert.Empty(bootstrap.RuntimeConfiguration);
    }

    [Fact]
    public void FromConfiguration_WithMinimalSqliteSetup_UsesServerDatabasePath()
    {
        var paths = CreatePaths();
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Setup:AdminPassword"] = "administrator-password"
        });

        var bootstrap = ConfiguredSetupBootstrap.FromConfiguration(configuration, paths);
        var connectionString = bootstrap.RuntimeConfiguration["Database:ConnectionString"];
        var connectionStringBuilder = new SqliteConnectionStringBuilder(connectionString);

        Assert.True(bootstrap.IsConfigured);
        Assert.Equal(DeploymentMode.Standalone, bootstrap.Request.DeploymentMode);
        Assert.Equal(DatabaseProvider.Sqlite, bootstrap.Request.Provider);
        Assert.Equal(paths.DatabaseFile, bootstrap.Request.SqlitePath);
        Assert.Equal(paths.DatabaseFile, connectionStringBuilder.DataSource);
        Assert.Equal("sqlite", bootstrap.RuntimeConfiguration["Database:Provider"]);
        Assert.Equal("InProcess", bootstrap.RuntimeConfiguration["Execution:Provider"]);
        Assert.False(bootstrap.RuntimeConfiguration.ContainsKey("DistributedLock:Provider"));
    }

    [Fact]
    public void FromConfiguration_WithClusterPostgresSetup_MapsRuntimeConfiguration()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Setup:DeploymentMode"] = "Cluster",
            ["Setup:Provider"] = "Postgres",
            ["Setup:PostgresHost"] = "postgres",
            ["Setup:PostgresPort"] = "5544",
            ["Setup:PostgresDatabase"] = "agw_cluster",
            ["Setup:PostgresUsername"] = "agw",
            ["Setup:PostgresPassword"] = "p;ass=word",
            ["Setup:AdminPassword"] = "administrator-password"
        });

        var bootstrap = ConfiguredSetupBootstrap.FromConfiguration(
            configuration,
            CreatePaths());
        var connectionString = bootstrap.RuntimeConfiguration["Database:ConnectionString"];
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString);

        Assert.Equal(DeploymentMode.Cluster, bootstrap.Request.DeploymentMode);
        Assert.Equal(DatabaseProvider.Postgres, bootstrap.Request.Provider);
        Assert.Equal("postgres", connectionStringBuilder.Host);
        Assert.Equal(5544, connectionStringBuilder.Port);
        Assert.Equal("agw_cluster", connectionStringBuilder.Database);
        Assert.Equal("agw", connectionStringBuilder.Username);
        Assert.Equal("p;ass=word", connectionStringBuilder.Password);
        Assert.Equal("postgres", bootstrap.RuntimeConfiguration["Database:Provider"]);
        Assert.Equal("Distributed", bootstrap.RuntimeConfiguration["Execution:Provider"]);
        Assert.Equal("postgres", bootstrap.RuntimeConfiguration["DistributedLock:Provider"]);
        Assert.Equal(string.Empty, bootstrap.RuntimeConfiguration["DistributedLock:ConnectionString"]);
    }

    [Fact]
    public void FromConfiguration_WithInvalidSetup_ThrowsWithoutIncludingPassword()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Setup:DeploymentMode"] = "Cluster",
            ["Setup:Provider"] = "Sqlite",
            ["Setup:AdminPassword"] = "administrator-password"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfiguredSetupBootstrap.FromConfiguration(configuration, CreatePaths()));

        Assert.Contains("Cluster deployments require PostgreSQL", exception.Message);
        Assert.DoesNotContain("administrator-password", exception.Message);
    }

    [Fact]
    public async Task InitializeIfConfiguredAsync_WhenSetupIsConfigured_InitializesOnce()
    {
        var bootstrap = ConfiguredSetupBootstrap.FromConfiguration(
            CreateConfiguration(new Dictionary<string, string?>
            {
                ["Setup:AdminPassword"] = "administrator-password"
            }),
            CreatePaths());
        var stateStore = new StubInitializationStateStore(isInitialized: false);
        var setupService = new StubSetupInitializationService();
        var initializer = new ConfiguredSetupInitializer(
            stateStore,
            setupService,
            bootstrap,
            NullLogger<ConfiguredSetupInitializer>.Instance);

        var initialized = await initializer.InitializeIfConfiguredAsync(
            TestContext.Current.CancellationToken);

        Assert.True(initialized);
        Assert.Same(bootstrap.Request, setupService.LastRequest);
    }

    [Fact]
    public async Task InitializeIfConfiguredAsync_WhenStateExists_DoesNotOverwriteState()
    {
        var bootstrap = ConfiguredSetupBootstrap.FromConfiguration(
            CreateConfiguration(new Dictionary<string, string?>
            {
                ["Setup:AdminPassword"] = "administrator-password"
            }),
            CreatePaths());
        var setupService = new StubSetupInitializationService();
        var initializer = new ConfiguredSetupInitializer(
            new StubInitializationStateStore(isInitialized: true),
            setupService,
            bootstrap,
            NullLogger<ConfiguredSetupInitializer>.Instance);

        var initialized = await initializer.InitializeIfConfiguredAsync(
            TestContext.Current.CancellationToken);

        Assert.False(initialized);
        Assert.Null(setupService.LastRequest);
    }

    private static IConfiguration CreateConfiguration(
        IReadOnlyDictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static AgwDataPaths CreatePaths()
    {
        return AgwDataPaths.Resolve(
            Path.Combine(Path.GetTempPath(), $"agw-configured-setup-{Guid.CreateVersion7():N}"),
            "/unused");
    }

    private sealed class StubInitializationStateStore : IInitializationStateStore
    {
        public StubInitializationStateStore(bool isInitialized)
        {
            IsInitialized = isInitialized;
        }

        public bool IsInitialized { get; }

        public Task PersistAsync(
            SetupConfiguration configuration,
            string passwordHash,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubSetupInitializationService : ISetupInitializationService
    {
        public SetupRequest? LastRequest { get; private set; }

        public Task InitializeAsync(
            SetupRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.CompletedTask;
        }
    }
}
