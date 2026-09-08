using Agw.Host.Hosting;
using Agw.Setup.Services;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agw.Host.Tests;

public sealed class ServerDeploymentConfigurationTests
{
    [Fact]
    public void Apply_WithoutStateOrExplicitSettings_UsesDefaults()
    {
        using var configuration = new ConfigurationManager();
        var paths = AgwDataPaths.Resolve(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), "/unused");
        var state = new JsonInitializationStateStore(paths);

        ServerDeploymentConfiguration.Apply(configuration, state.GetLegacyDeploymentConfiguration());

        Assert.False(state.IsInitialized);
        Assert.False(File.Exists(paths.StateFile));
        Assert.Equal("sqlite", configuration["Database:Provider"]);
        Assert.Equal("Data Source=agw.db", configuration["Database:ConnectionString"]);
        Assert.Equal("InProcess", configuration["Execution:Provider"]);
        Assert.Null(configuration["DistributedLock:Provider"]);
        Assert.Equal(string.Empty, configuration["DistributedLock:ConnectionString"]);
    }

    [Fact]
    public void Apply_PackagedDefaults_DoNotOverrideLegacyClusterConfiguration()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        var legacy = LegacyClusterConfiguration();

        ServerDeploymentConfiguration.Apply(configuration, legacy);

        foreach (var entry in legacy)
            Assert.Equal(entry.Value, configuration[entry.Key]);
        ServerDeploymentConfiguration.Validate(AgwHostProfile.DataPlane, configuration, isInitialized: true);
    }

    [Fact]
    public void Apply_StandardSources_OverrideLegacyValuesInTheirOriginalOrder()
    {
        var prefix = $"AGW_CONFIG_TEST_{Guid.NewGuid():N}_";
        var variable = prefix + "Database__ConnectionString";
        var directory = Path.Combine(Path.GetTempPath(), prefix);
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(variable, "Host=environment-variable");
        try
        {
            using var configuration = new ConfigurationManager();
            var baseJson = Path.Combine(directory, "appsettings.json");
            var environmentJson = Path.Combine(directory, "appsettings.Test.json");
            File.WriteAllText(
                baseJson,
                """{"Database":{"Provider":"sqlite","ConnectionString":"Data Source=explicit.db"}}"""
            );
            File.WriteAllText(
                environmentJson,
                """{"Database":{"Provider":"postgres","ConnectionString":"Host=environment-json"}}"""
            );
            configuration.AddJsonFile(baseJson);
            ServerDeploymentConfiguration.Apply(configuration, LegacyClusterConfiguration());
            Assert.Equal("sqlite", configuration["Database:Provider"]);
            Assert.Equal("Data Source=explicit.db", configuration["Database:ConnectionString"]);

            configuration.AddJsonFile(environmentJson);
            Assert.Equal("postgres", configuration["Database:Provider"]);
            Assert.Equal("Host=environment-json", configuration["Database:ConnectionString"]);
            configuration.AddEnvironmentVariables(prefix);
            Assert.Equal("Host=environment-variable", configuration["Database:ConnectionString"]);
            configuration.AddCommandLine(["--Database:ConnectionString=Host=command-line"]);

            Assert.Equal("Host=command-line", configuration["Database:ConnectionString"]);
            Assert.Equal("Distributed", configuration["Execution:Provider"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(AgwHostProfile.ControlPlane, false)]
    [InlineData(AgwHostProfile.ControlPlane, true)]
    [InlineData(AgwHostProfile.DataPlane, true)]
    public void Validate_ClusterConfiguration_AcceptsInitializedAndFirstRunControlPlane(
        AgwHostProfile profile,
        bool initialized
    )
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(LegacyClusterConfiguration());
        ServerDeploymentConfiguration.Apply(configuration, new Dictionary<string, string?>());

        var exception = Record.Exception(() =>
            ServerDeploymentConfiguration.Validate(profile, configuration, initialized)
        );

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("Database:Provider", "sqlite")]
    [InlineData("Execution:Provider", "InProcess")]
    [InlineData("DistributedLock:Provider", "inmemory")]
    public void Validate_UninitializedControlPlane_StillRejectsInvalidDeployment(string key, string value)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(LegacyClusterConfiguration());
        configuration[key] = value;

        var exception = Assert.Throws<AgwException>(() =>
            ServerDeploymentConfiguration.Validate(AgwHostProfile.ControlPlane, configuration, isInitialized: false)
        );

        Assert.Equal(ErrorCodes.InvalidSetupConfiguration.Code, exception.Code);
    }

    [Fact]
    public void Validate_DataPlaneWithoutInitializedAuthenticationState_RejectsStartup()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(LegacyClusterConfiguration());

        var exception = Assert.Throws<AgwException>(() =>
            ServerDeploymentConfiguration.Validate(AgwHostProfile.DataPlane, configuration, isInitialized: false)
        );

        Assert.Equal(ErrorCodes.InvalidSetupConfiguration.Code, exception.Code);
        Assert.Contains("Complete Control Plane setup first", exception.Message);
    }

    [Theory]
    [InlineData(AgwHostProfile.Standalone, false)]
    [InlineData(AgwHostProfile.ControlPlane, false)]
    [InlineData(AgwHostProfile.DataPlane, true)]
    public void Validate_PostgresWithInheritedSqliteConnectionString_RejectsBeforeStartup(
        AgwHostProfile profile,
        bool initialized
    )
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["Execution:Provider"] = "Distributed",
            }
        );
        ServerDeploymentConfiguration.Apply(configuration, new Dictionary<string, string?>());

        var exception = Assert.Throws<AgwException>(() =>
            ServerDeploymentConfiguration.Validate(profile, configuration, initialized)
        );

        Assert.Equal(ErrorCodes.InvalidSetupConfiguration.Code, exception.Code);
        Assert.Contains("Database:ConnectionString", exception.Message);
        Assert.Contains("PostgreSQL", exception.Message);
    }

    [Theory]
    [InlineData("postgres", null)]
    [InlineData("postgres", "")]
    [InlineData("postgres", " ")]
    [InlineData("postgres", "Database=agw;Password=secret-marker")]
    [InlineData("postgres", "Host= ;Password=secret-marker")]
    [InlineData("postgres", "Host=localhost;Port=secret-marker")]
    [InlineData("postgres", "Host=localhost;Port=999999999999999999999;Password=secret-marker")]
    [InlineData("postgres", "Host=localhost;secret-marker=value")]
    [InlineData("postgres", "Host=localhost;Password=\"secret-marker")]
    [InlineData("sqlite", "Host=localhost;Password=secret-marker")]
    [InlineData("sqlite", "Data Source=agw.db;Mode=secret-marker")]
    [InlineData("sqlite", "Data Source=\"secret-marker")]
    public void Validate_InvalidDatabaseConnectionString_ReportsConfigurationKeyWithoutSecrets(
        string provider,
        string? connectionString
    )
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider,
                ["Database:ConnectionString"] = connectionString,
            }
        );

        var exception = Assert.Throws<AgwException>(() =>
            ServerDeploymentConfiguration.Validate(AgwHostProfile.Standalone, configuration, isInitialized: false)
        );

        Assert.Equal(ErrorCodes.InvalidSetupConfiguration.Code, exception.Code);
        Assert.Contains("Database:ConnectionString", exception.Message);
        Assert.DoesNotContain("secret-marker", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("sqlite", null)]
    [InlineData("sqlite", "")]
    [InlineData("sqlite", " ")]
    [InlineData("sqlite", "Data Source=custom.db")]
    [InlineData("sqlite", "Data Source=:memory:")]
    [InlineData("postgres", "Host=unresolvable.invalid")]
    [InlineData("postgres", "Host=/tmp/nonexistent-postgres-socket")]
    [InlineData("postgres", "Host=first.invalid,second.invalid;Port=5432")]
    [InlineData("postgres", "Host=localhost;Passfile=/tmp/nonexistent.pgpass")]
    [InlineData("postgres", "Host=localhost;Password=\"p;ass=word\";Application Name=custom-service")]
    public void Validate_ValidDatabaseConnectionString_PreservesProviderDefaultsAndUserValues(
        string provider,
        string? connectionString
    )
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider,
                ["Database:ConnectionString"] = connectionString,
            }
        );

        var exception = Record.Exception(() =>
            ServerDeploymentConfiguration.Validate(AgwHostProfile.Standalone, configuration, isInitialized: false)
        );

        Assert.Null(exception);
        Assert.Equal(connectionString, configuration["Database:ConnectionString"]);
    }

    private static Dictionary<string, string?> LegacyClusterConfiguration() =>
        new()
        {
            ["Database:Provider"] = "postgres",
            ["Database:ConnectionString"] = "Host=legacy",
            ["Execution:Provider"] = "Distributed",
            ["DistributedLock:Provider"] = "postgres",
            ["DistributedLock:ConnectionString"] = "Host=legacy-locks",
        };

    [Fact]
    public async Task StateRefresh_UpdatesAuthenticationWithoutChangingStartupDeployment()
    {
        var paths = AgwDataPaths.Resolve(Path.Combine(Path.GetTempPath(), $"agw-legacy-{Guid.NewGuid():N}"), "/unused");
        paths.EnsureCreated();
        try
        {
            const string original = """
                {"schemaVersion":2,"isInitialized":true,"database":{"provider":"postgres","connectionString":"Host=original"},"passwordHash":"old-hash","sessionVersion":1}
                """;
            await File.WriteAllTextAsync(paths.StateFile, original, TestContext.Current.CancellationToken);
            var state = new JsonInitializationStateStore(paths);
            using var configuration = new ConfigurationManager();
            ServerDeploymentConfiguration.Apply(configuration, state.GetLegacyDeploymentConfiguration());

            await File.WriteAllTextAsync(
                paths.StateFile,
                original.Replace("Host=original", "Host=changed").Replace("old-hash", "new-hash"),
                TestContext.Current.CancellationToken
            );
            await state.RefreshAsync(TestContext.Current.CancellationToken);

            Assert.Equal("new-hash", state.GetAuthenticationSnapshot().PasswordHash);
            Assert.Equal("Host=original", configuration["Database:ConnectionString"]);
            Assert.Null(configuration["PasswordHash"]);
            Assert.Null(configuration["SessionVersion"]);
            Assert.Null(configuration["IsInitialized"]);
            Assert.Equal(
                "Host=changed",
                new JsonInitializationStateStore(paths).GetLegacyDeploymentConfiguration()["Database:ConnectionString"]
            );
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }
}
