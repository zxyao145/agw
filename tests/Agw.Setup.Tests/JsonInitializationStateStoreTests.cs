using Agw.Auth.Contracts;
using Agw.Auth.Extensions;
using Agw.Setup.Services;
using Agw.Shared.Runtime;
using Agw.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agw.Setup.Tests;

public class JsonInitializationStateStoreTests
{
    [Fact]
    public async Task PersistAsync_WhenInitialized_WritesReloadableServerState()
    {
        var paths = CreatePaths();
        try
        {
            var store = new JsonInitializationStateStore(paths);

            await store.PersistAsync("hashed-password", TestContext.Current.CancellationToken);

            var reloadedStore = new JsonInitializationStateStore(paths);
            var reloaded = reloadedStore.GetAuthenticationSnapshot();
            Assert.True(reloadedStore.IsInitialized);
            Assert.Equal("hashed-password", reloaded.PasswordHash);
            Assert.Equal(1, reloaded.SessionVersion);
            Assert.Empty(reloadedStore.GetLegacyDeploymentConfiguration());
            Assert.True(File.Exists(paths.StateFile));
            var persistedJson = await File.ReadAllTextAsync(paths.StateFile, TestContext.Current.CancellationToken);
            Assert.Contains("\"schemaVersion\": 3", persistedJson);
            Assert.DoesNotContain("\"database\"", persistedJson);
            Assert.DoesNotContain("\"execution\"", persistedJson);
            Assert.DoesNotContain("\"distributedLock\"", persistedJson);
            Assert.DoesNotContain("\"tokens\"", persistedJson);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.StateFile));
            }
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ClearLegacyApiTokensAsync_WhenLegacyTokensExist_RemovesOnlyTokens()
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
                  "tokens": [
                    {
                      "id": "0198b7b8-a50c-7f6e-a50d-b46e722a6622",
                      "name": "Automation",
                      "prefix": "agw_legacy12",
                      "secretHash": "ABCD",
                      "createdAt": "2026-07-13T10:00:00+00:00"
                    }
                  ]
                }
                """,
                TestContext.Current.CancellationToken
            );
            var store = new JsonInitializationStateStore(paths);

            var legacyToken = Assert.Single(store.GetLegacyApiTokens());
            await store.ClearLegacyApiTokensAsync(TestContext.Current.CancellationToken);

            Assert.Equal("Automation", legacyToken.Name);
            Assert.Equal("old-hash", store.GetAuthenticationSnapshot().PasswordHash);
            Assert.Equal(
                "postgres",
                new JsonInitializationStateStore(paths).GetLegacyDeploymentConfiguration()["Database:Provider"]
            );
            Assert.Empty(store.GetLegacyApiTokens());
            Assert.DoesNotContain(
                "\"tokens\"",
                await File.ReadAllTextAsync(paths.StateFile, TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task AuthenticationWrites_WhenLoadingSchemaVersionTwo_PreserveAllLegacyDeploymentKeys()
    {
        var paths = CreatePaths();
        try
        {
            await File.WriteAllTextAsync(
                paths.StateFile,
                """
                {
                  "schemaVersion": 2,
                  "isInitialized": true,
                  "database": { "provider": "postgres", "connectionString": "Host=db;Database=agw" },
                  "execution": { "provider": "distributed" },
                  "distributedLock": { "provider": "postgres", "connectionString": "" },
                  "passwordHash": "old-hash",
                  "sessionVersion": 3,
                  "tokens": []
                }
                """,
                TestContext.Current.CancellationToken
            );
            var store = new JsonInitializationStateStore(paths);
            var originalConfiguration = store.GetLegacyDeploymentConfiguration();

            await store.UpdatePasswordAsync("new-hash", TestContext.Current.CancellationToken);
            await store.ClearLegacyApiTokensAsync(TestContext.Current.CancellationToken);

            var reloaded = new JsonInitializationStateStore(paths);
            Assert.Equal(originalConfiguration, reloaded.GetLegacyDeploymentConfiguration());
            Assert.Equal(5, originalConfiguration.Count);
            Assert.Equal("distributed", originalConfiguration["Execution:Provider"]);
            Assert.Equal("postgres", originalConfiguration["DistributedLock:Provider"]);
            Assert.Equal(string.Empty, originalConfiguration["DistributedLock:ConnectionString"]);
            Assert.Equal("new-hash", reloaded.GetAuthenticationSnapshot().PasswordHash);
            Assert.Equal(4, reloaded.GetAuthenticationSnapshot().SessionVersion);
            var configuration = new ConfigurationBuilder().AddJsonFile(paths.StateFile).Build();
            Assert.Equal("2", configuration["SchemaVersion"]);
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
                TestContext.Current.CancellationToken
            );
            var store = new JsonInitializationStateStore(paths);

            await store.UpdatePasswordAsync("new-hash", TestContext.Current.CancellationToken);
            var reloaded = new JsonInitializationStateStore(paths);

            Assert.Equal("postgres", reloaded.GetLegacyDeploymentConfiguration()["Database:Provider"]);
            Assert.Equal(
                "Host=db;Database=agw",
                reloaded.GetLegacyDeploymentConfiguration()["Database:ConnectionString"]
            );
            Assert.Equal("new-hash", reloaded.GetAuthenticationSnapshot().PasswordHash);
            var persistedJson = await File.ReadAllTextAsync(paths.StateFile, TestContext.Current.CancellationToken);
            Assert.Contains("\"schemaVersion\": 1", persistedJson);
            Assert.DoesNotContain("\"execution\"", persistedJson);
            Assert.DoesNotContain("\"distributedLock\"", persistedJson);
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

    [Fact]
    public async Task RefreshAsync_WhenAnotherProcessUpdatesState_RefreshesCachedSnapshot()
    {
        var paths = CreatePaths();
        try
        {
            var now = new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);
            var writerClock = new TestTimeProvider(now);
            var writer = new JsonInitializationStateStore(paths, writerClock);
            await writer.PersistAsync("initial-hash", TestContext.Current.CancellationToken);
            var reader = new JsonInitializationStateStore(paths, new TestTimeProvider(now));

            await writer.UpdatePasswordAsync("updated-hash", TestContext.Current.CancellationToken);
            Assert.Equal("initial-hash", reader.GetAuthenticationSnapshot().PasswordHash);

            await reader.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal("updated-hash", reader.GetAuthenticationSnapshot().PasswordHash);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task AuthenticationSnapshot_WhenStateFileIsExclusivelyLocked_UsesCachedState()
    {
        var paths = CreatePaths();
        try
        {
            var store = new JsonInitializationStateStore(paths);
            await store.PersistAsync("cached-hash", TestContext.Current.CancellationToken);
            using var lockedFile = new FileStream(paths.StateFile, FileMode.Open, FileAccess.Read, FileShare.None);

            var snapshot = store.GetAuthenticationSnapshot();

            Assert.Equal("cached-hash", snapshot.PasswordHash);
            Assert.True(store.IsInitialized);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdatePasswordAsync_OnWindows_RetriesUntilConflictingReaderCloses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = CreatePaths();
        try
        {
            var store = new JsonInitializationStateStore(paths);
            await store.PersistAsync("initial-hash", TestContext.Current.CancellationToken);
            using var lockedFile = new FileStream(paths.StateFile, FileMode.Open, FileAccess.Read, FileShare.Read);

            var update = store.UpdatePasswordAsync("updated-hash", TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
            Assert.False(update.IsCompleted);

            lockedFile.Dispose();
            await update.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("updated-hash", store.GetAuthenticationSnapshot().PasswordHash);
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public void AddSetup_ReadOnly_ExposesOnlyStateReaders()
    {
        var paths = CreatePaths();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(paths);
            services.AddAuth();
            services.AddSetup(new ConfigurationBuilder().Build(), readOnly: true);
            using var provider = services.BuildServiceProvider();

            var readers = provider.GetServices<IAuthenticationStateReader>().ToArray();
            Assert.Single(readers);
            Assert.IsType<JsonInitializationStateStore>(readers[0]);
            Assert.NotNull(provider.GetRequiredService<IServerInitializationState>());
            Assert.Contains(
                provider.GetServices<IHostedService>(),
                service => service.GetType().Name == "JsonInitializationStateRefreshHostedService"
            );
            Assert.Null(provider.GetService<IAuthenticationStateStore>());
            Assert.Null(provider.GetService<IInitializationStateStore>());
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
