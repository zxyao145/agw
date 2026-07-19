using Agw.Auth.Application;
using Agw.Setup.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;
using Agw.Testing;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Agw.Setup.Tests;

public class JsonInitializationStateStoreTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider TimeProvider = new TestTimeProvider(UtcNow);

    [Fact]
    public async Task PersistAsync_WhenInitialized_WritesReloadableServerState()
    {
        var paths = CreatePaths();
        try
        {
            var store = new JsonInitializationStateStore(paths, TimeProvider);

            await store.PersistAsync(new SetupRequest
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = "Data Source=agw.db",
                AdminPassword = "password-password"
            }, "hashed-password", TestContext.Current.CancellationToken);

            var reloadedStore = new JsonInitializationStateStore(paths, TimeProvider);
            var reloaded = reloadedStore.GetAuthenticationSnapshot();
            Assert.True(reloadedStore.IsInitialized);
            Assert.Equal("hashed-password", reloaded.PasswordHash);
            Assert.Equal(1, reloaded.SessionVersion);
            Assert.Equal(DatabaseProvider.Sqlite, reloadedStore.DatabaseProvider);
            Assert.True(File.Exists(paths.StateFile));
            Assert.Contains(
                "\"provider\": \"sqlite\"",
                await File.ReadAllTextAsync(paths.StateFile, TestContext.Current.CancellationToken));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(paths.StateFile));
            }
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateTokenAsync_ReturnsSecretOnceAndStoresOnlyHash()
    {
        var paths = CreatePaths();
        try
        {
            var store = new JsonInitializationStateStore(paths, TimeProvider);
            await store.PersistAsync(new SetupRequest
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = "Data Source=agw.db",
                AdminPassword = "password-password"
            }, "hashed-password", TestContext.Current.CancellationToken);

            var created = await store.CreateTokenAsync("Mobile", TestContext.Current.CancellationToken);

            Assert.StartsWith("agw_", created.Token);
            Assert.Equal(UtcNow, created.CreatedAt);
            Assert.True(store.ValidateToken(created.Token));
            Assert.DoesNotContain(created.Token, await File.ReadAllTextAsync(paths.StateFile, TestContext.Current.CancellationToken));
            Assert.Single(store.GetAuthenticationSnapshot().Tokens);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RevokeTokenAsync_WhenTokenExists_InvalidatesSecret()
    {
        var paths = CreatePaths();
        try
        {
            var store = new JsonInitializationStateStore(paths, TimeProvider);
            await store.PersistAsync(new SetupRequest
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = "Data Source=agw.db",
                AdminPassword = "password-password"
            }, "hashed-password", TestContext.Current.CancellationToken);
            var created = await store.CreateTokenAsync("CLI", TestContext.Current.CancellationToken);

            var revoked = await store.RevokeTokenAsync(created.Id, TestContext.Current.CancellationToken);

            Assert.True(revoked);
            Assert.False(store.ValidateToken(created.Token));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task AuthenticationWrites_WhenLoadingSchemaVersionOne_PreserveDatabaseConfiguration()
    {
        var paths = CreatePaths();
        try
        {
            await File.WriteAllTextAsync(
                paths.StateFile,
                """
                {
                  "schemaVersion": 1,
                  "isInitialized": true,
                  "database": {
                    "provider": "postgres",
                    "connectionString": "Host=db;Database=agw"
                  },
                  "passwordHash": "old-hash",
                  "sessionVersion": 3,
                  "tokens": []
                }
                """,
                TestContext.Current.CancellationToken);
            var store = new JsonInitializationStateStore(paths, TimeProvider);

            var token = await store.CreateTokenAsync("Automation", TestContext.Current.CancellationToken);
            await store.UpdatePasswordAsync("new-hash", TestContext.Current.CancellationToken);
            var reloaded = new JsonInitializationStateStore(paths, TimeProvider);

            Assert.Equal(DatabaseProvider.Postgres, reloaded.DatabaseProvider);
            Assert.Equal("Host=db;Database=agw", reloaded.DatabaseConnectionString);
            Assert.Equal("new-hash", reloaded.GetAuthenticationSnapshot().PasswordHash);
            Assert.True(reloaded.ValidateToken(token.Token));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public void AddSetup_ResolvesAllStateInterfacesToSameAdapter()
    {
        var paths = CreatePaths();
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(paths);
            services.AddSingleton(TimeProvider);
            services.AddSetup(new ConfigurationBuilder().Build());
            using var provider = services.BuildServiceProvider();

            var setupState = provider.GetRequiredService<IInitializationStateStore>();
            var authenticationState = provider.GetRequiredService<IAuthenticationStateStore>();
            var serverState = provider.GetRequiredService<IServerInitializationState>();

            Assert.Same(setupState, authenticationState);
            Assert.Same(setupState, serverState);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static AgwDataPaths CreatePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-state-{Guid.CreateVersion7():N}");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        paths.EnsureCreated();
        return paths;
    }
}
