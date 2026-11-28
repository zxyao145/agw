using System.Text.Json;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Coordination;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Medallion.Threading.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agw.Jobs.Tests;

[Trait("Category", "Postgres")]
public sealed class ApplicationLockRouterPostgresTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryAcquireExclusiveAsync_SharedOnOtherRouter_ReturnsNull()
    {
        var connectionString = RequireConnectionString();
        using var settings = CreateSettings(connectionString);
        var firstRouter = CreateRouter(settings);
        var secondRouter = CreateRouter(settings);
        var resourceName = $"application-lock-{Guid.CreateVersion7():N}";

        {
            await using var shared = await firstRouter.AcquireSharedAsync(resourceName, Token);

            Assert.Null(await secondRouter.TryAcquireExclusiveAsync(resourceName, Token));
        }

        await using var exclusive = Assert.IsAssignableFrom<IApplicationLockLease>(
            await secondRouter.TryAcquireExclusiveAsync(resourceName, Token)
        );
    }

    [Fact]
    public async Task AcquireSharedAsync_ExclusiveOnOtherRouter_WaitsForRelease()
    {
        var connectionString = RequireConnectionString();
        using var settings = CreateSettings(connectionString);
        var firstRouter = CreateRouter(settings);
        var secondRouter = CreateRouter(settings);
        var resourceName = $"application-lock-{Guid.CreateVersion7():N}";
        var exclusive = Assert.IsAssignableFrom<IApplicationLockLease>(
            await firstRouter.TryAcquireExclusiveAsync(resourceName, Token)
        );

        Task<IApplicationLockLease> sharedTask;
        try
        {
            sharedTask = secondRouter.AcquireSharedAsync(resourceName, Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100), Token);
            Assert.False(sharedTask.IsCompleted);
        }
        finally
        {
            await exclusive.DisposeAsync();
        }

        await using var shared = await sharedTask.WaitAsync(TimeSpan.FromSeconds(10), Token);
    }

    private static string RequireConnectionString()
    {
        var environmentConnectionString = Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(environmentConnectionString))
        {
            return environmentConnectionString;
        }

        var path = Path.Combine(FindRepositoryRoot(), ".scratch", "postgres-tests.json");
        if (!File.Exists(path))
        {
            Assert.Skip("PostgreSQL test configuration is not present.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var connectionString = document.RootElement.GetProperty("ConnectionString").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        return connectionString!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the Agw solution root.");
    }

    private static ServiceProvider CreateSettings(string connectionString) =>
        new ServiceCollection()
            .Configure<DatabaseSettings>(settings =>
            {
                settings.Provider = DatabaseProvider.Postgres;
                settings.ConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    ApplicationName = $"agw-application-lock-{Guid.CreateVersion7():N}",
                    Pooling = false,
                }.ConnectionString;
            })
            .AddOptions<DistributedLockSettings>()
            .Services.BuildServiceProvider();

    private static ApplicationLockRouter CreateRouter(IServiceProvider settings) =>
        new(
            settings.GetRequiredService<IOptionsMonitor<DatabaseSettings>>(),
            settings.GetRequiredService<IOptionsMonitor<DistributedLockSettings>>(),
            new InMemoryApplicationLock(),
            (_, value) => new PostgresDistributedSynchronizationProvider(value)
        );
}
