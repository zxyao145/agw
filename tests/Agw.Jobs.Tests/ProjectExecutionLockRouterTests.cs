using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Coordination;
using Agw.Infrastructure.Jobs;
using Agw.Infrastructure.Skills;
using Agw.Shared.Configuration;
using Agw.Shared.Coordination;
using Medallion.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Jobs.Tests;

public class ProjectExecutionLockRouterTests
{
    [Fact]
    public async Task AcquireAsync_WhenConfigurationIsBlankAndDatabaseIsSqlite_UsesInMemoryLock()
    {
        var state = new DatabaseSettings { Provider = DatabaseProvider.Sqlite };
        var projectLock = CreateRouter(
            state,
            new MutableOptionsMonitor<DistributedLockSettings>(new DistributedLockSettings()),
            (_, _) => throw new Xunit.Sdk.XunitException("Distributed provider should not be created.")
        );
        var projectId = Guid.CreateVersion7();
        var firstLease = await projectLock.AcquireAsync(projectId, TestContext.Current.CancellationToken);

        var secondLeaseTask = projectLock.AcquireAsync(projectId, TestContext.Current.CancellationToken);

        Assert.False(secondLeaseTask.IsCompleted);
        await firstLease.DisposeAsync();
        await using var secondLease = await secondLeaseTask.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task AcquireAsync_WhenInMemoryIsExplicit_OverridesPostgresDatabase()
    {
        var state = new DatabaseSettings { Provider = DatabaseProvider.Postgres, ConnectionString = "Host=database" };
        var settings = new DistributedLockSettings { Provider = DistributedLockProvider.InMemory };
        var projectLock = CreateRouter(
            state,
            new MutableOptionsMonitor<DistributedLockSettings>(settings),
            (_, _) => throw new Xunit.Sdk.XunitException("Distributed provider should not be created.")
        );

        await using var lease = await projectLock.AcquireAsync(
            Guid.CreateVersion7(),
            TestContext.Current.CancellationToken
        );
    }

    [Fact]
    public async Task AcquireAsync_WhenPostgresIsExplicit_UsesConfiguredDistributedLock()
    {
        var state = new DatabaseSettings
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = "Data Source=agw.db",
        };
        var settings = new DistributedLockSettings
        {
            Provider = DistributedLockProvider.Postgres,
            ConnectionString = "Host=locks",
        };
        var provider = new RecordingDistributedLockProvider();
        DistributedLockProvider? createdProvider = null;
        string? createdConnectionString = null;
        var projectLock = CreateRouter(
            state,
            new MutableOptionsMonitor<DistributedLockSettings>(settings),
            (providerName, connectionString) =>
            {
                createdProvider = providerName;
                createdConnectionString = connectionString;
                return provider;
            }
        );
        var projectId = Guid.CreateVersion7();
        using var cancellation = new CancellationTokenSource();

        await using (await projectLock.AcquireAsync(projectId, cancellation.Token)) { }

        Assert.Equal(DistributedLockProvider.Postgres, createdProvider);
        Assert.Equal("Host=locks", createdConnectionString);
        Assert.Equal($"agw:jobs:project-lock:{projectId:D}", provider.LastLockName);
        Assert.Equal(cancellation.Token, provider.LastCancellationToken);
        Assert.True(provider.LastHandle?.IsDisposed);
    }

    [Fact]
    public async Task AcquireAsync_WhenConfigurationIsBlankAndDatabaseChanges_UpdatesWithoutRestart()
    {
        var state = new DatabaseSettings
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = "Data Source=agw.db",
        };
        var createdConnectionStrings = new List<string>();
        var projectLock = CreateRouter(
            state,
            new MutableOptionsMonitor<DistributedLockSettings>(new DistributedLockSettings()),
            (_, connectionString) =>
            {
                createdConnectionStrings.Add(connectionString);
                return new RecordingDistributedLockProvider();
            }
        );
        await using (await projectLock.AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)) { }

        state.Provider = DatabaseProvider.Postgres;
        state.ConnectionString = "Host=database";
        await using (await projectLock.AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)) { }

        Assert.Equal(["Host=database"], createdConnectionStrings);
    }

    [Fact]
    public async Task AcquireAsync_WhenOptionsChange_ReplacesCachedProviderOnlyForEffectiveChange()
    {
        var state = new DatabaseSettings { Provider = DatabaseProvider.Postgres, ConnectionString = "Host=database" };
        var options = new MutableOptionsMonitor<DistributedLockSettings>(new DistributedLockSettings());
        var createdConfigurations = new List<(DistributedLockProvider Provider, string ConnectionString)>();
        var projectLock = CreateRouter(
            state,
            options,
            (provider, connectionString) =>
            {
                createdConfigurations.Add((provider, connectionString));
                return new RecordingDistributedLockProvider();
            }
        );

        await using (await projectLock.AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)) { }
        await using (await projectLock.AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)) { }
        options.CurrentValue = new DistributedLockSettings
        {
            Provider = DistributedLockProvider.Postgres,
            ConnectionString = "Host=locks",
        };
        await using (await projectLock.AcquireAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)) { }

        Assert.Equal(
            [(DistributedLockProvider.Postgres, "Host=database"), (DistributedLockProvider.Postgres, "Host=locks")],
            createdConfigurations
        );
    }

    private static ProjectExecutionLockRouter CreateRouter(
        DatabaseSettings state,
        IOptionsMonitor<DistributedLockSettings> options,
        Func<DistributedLockProvider, string, IDistributedLockProvider> providerFactory
    )
    {
        return new ProjectExecutionLockRouter(
            new MutableOptionsMonitor<DatabaseSettings>(state),
            options,
            new InMemoryProjectExecutionLock(),
            providerFactory
        );
    }

    [Theory]
    [InlineData("application")]
    [InlineData("skill")]
    public async Task AcquireAsync_OtherLockRouters_FollowDatabaseOptionsWithoutInitializationState(string kind)
    {
        var database = new MutableOptionsMonitor<DatabaseSettings>(
            new DatabaseSettings { Provider = DatabaseProvider.Postgres, ConnectionString = "Host=first" }
        );
        var settings = new MutableOptionsMonitor<DistributedLockSettings>(new DistributedLockSettings());
        var connections = new List<string>();
        IDistributedLockProvider CreateProvider(DistributedLockProvider provider, string connectionString)
        {
            Assert.Equal(DistributedLockProvider.Postgres, provider);
            connections.Add(connectionString);
            return new RecordingDistributedLockProvider();
        }

        Func<Task<IAsyncDisposable>> acquire;
        if (kind == "application")
        {
            var router = new ApplicationLockRouter(database, settings, new InMemoryApplicationLock(), CreateProvider);
            acquire = async () => await router.AcquireAsync("test", TestContext.Current.CancellationToken);
        }
        else
        {
            var router = new RemoteSkillRefreshLockRouter(
                database,
                settings,
                CreateProvider,
                NullLogger<RemoteSkillRefreshLockRouter>.Instance
            );
            acquire = () => router.AcquireAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        }

        await using (await acquire()) { }
        database.CurrentValue = new DatabaseSettings
        {
            Provider = DatabaseProvider.Postgres,
            ConnectionString = "Host=second",
        };
        await using (await acquire()) { }

        Assert.Equal(["Host=first", "Host=second"], connections);
    }

    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public MutableOptionsMonitor(T currentValue)
        {
            CurrentValue = currentValue;
        }

        public T CurrentValue { get; set; }

        public T Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            return null;
        }
    }

    private sealed class RecordingDistributedLockProvider : IDistributedLockProvider
    {
        public string? LastLockName { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public RecordingDistributedSynchronizationHandle? LastHandle { get; private set; }

        public IDistributedLock CreateLock(string name)
        {
            LastLockName = name;
            return new RecordingDistributedLock(this, name);
        }

        private sealed class RecordingDistributedLock : IDistributedLock
        {
            private readonly RecordingDistributedLockProvider _owner;

            public RecordingDistributedLock(RecordingDistributedLockProvider owner, string name)
            {
                _owner = owner;
                Name = name;
            }

            public string Name { get; }

            public IDistributedSynchronizationHandle? TryAcquire(
                TimeSpan timeout = default,
                CancellationToken cancellationToken = default
            )
            {
                return Acquire(timeout, cancellationToken);
            }

            public IDistributedSynchronizationHandle Acquire(
                TimeSpan? timeout = null,
                CancellationToken cancellationToken = default
            )
            {
                _owner.LastCancellationToken = cancellationToken;
                return _owner.LastHandle = new RecordingDistributedSynchronizationHandle();
            }

            public async ValueTask<IDistributedSynchronizationHandle?> TryAcquireAsync(
                TimeSpan timeout = default,
                CancellationToken cancellationToken = default
            )
            {
                return await AcquireAsync(timeout, cancellationToken);
            }

            public ValueTask<IDistributedSynchronizationHandle> AcquireAsync(
                TimeSpan? timeout = null,
                CancellationToken cancellationToken = default
            )
            {
                return ValueTask.FromResult(Acquire(timeout, cancellationToken));
            }
        }
    }

    private sealed class RecordingDistributedSynchronizationHandle : IDistributedSynchronizationHandle
    {
        public CancellationToken HandleLostToken => CancellationToken.None;
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
