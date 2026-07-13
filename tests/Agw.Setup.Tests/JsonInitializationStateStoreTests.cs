using Agw.Setup.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

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

            await store.PersistAsync(new SetupRequest
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = "Data Source=agw.db",
                AdminPassword = "password-password"
            }, "hashed-password", TestContext.Current.CancellationToken);

            var reloadedStore = new JsonInitializationStateStore(paths);
            var reloaded = reloadedStore.GetSnapshot();
            Assert.True(reloaded.IsInitialized);
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
            var store = new JsonInitializationStateStore(paths);
            await store.PersistAsync(new SetupRequest
            {
                Provider = DatabaseProvider.Sqlite,
                ConnectionString = "Data Source=agw.db",
                AdminPassword = "password-password"
            }, "hashed-password", TestContext.Current.CancellationToken);

            var created = await store.CreateTokenAsync("Mobile", TestContext.Current.CancellationToken);

            Assert.StartsWith("agw_", created.Token);
            Assert.True(store.ValidateToken(created.Token));
            Assert.DoesNotContain(created.Token, await File.ReadAllTextAsync(paths.StateFile, TestContext.Current.CancellationToken));
            Assert.Single(store.GetSnapshot().Tokens);
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
            var store = new JsonInitializationStateStore(paths);
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

    private static AgwDataPaths CreatePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-state-{Guid.NewGuid():N}");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        paths.EnsureCreated();
        return paths;
    }
}
