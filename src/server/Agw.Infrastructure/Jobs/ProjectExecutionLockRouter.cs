using Agw.Infrastructure.Configuration;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Runtime;
using Medallion.Threading;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Jobs;

public sealed class ProjectExecutionLockRouter : IProjectExecutionLock
{
    private readonly IServerInitializationState _serverInitializationState;
    private readonly IOptionsMonitor<DistributedLockSettings> _settings;
    private readonly InMemoryProjectExecutionLock _inMemoryLock;
    private readonly Func<DistributedLockProvider, string, IDistributedLockProvider> _providerFactory;
    private readonly object _distributedLockSync = new();

    private DistributedLockProvider? _distributedProvider;
    private string? _distributedConnectionString;
    private DistributedProjectExecutionLock? _distributedLock;

    public ProjectExecutionLockRouter(
        IServerInitializationState serverInitializationState,
        IOptionsMonitor<DistributedLockSettings> settings,
        InMemoryProjectExecutionLock inMemoryLock,
        Func<DistributedLockProvider, string, IDistributedLockProvider> providerFactory
    )
    {
        _serverInitializationState = serverInitializationState;
        _settings = settings;
        _inMemoryLock = inMemoryLock;
        _providerFactory = providerFactory;
    }

    public Task<IAsyncDisposable> AcquireAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var effectiveSettings = DistributedLockSettingsResolver.Resolve(
            _settings.CurrentValue,
            _serverInitializationState.DatabaseProvider,
            _serverInitializationState.DatabaseConnectionString
        );

        return effectiveSettings.Provider == DistributedLockProvider.InMemory
            ? _inMemoryLock.AcquireAsync(projectId, cancellationToken)
            : GetDistributedLock(effectiveSettings.Provider!.Value, effectiveSettings.ConnectionString!)
                .AcquireAsync(projectId, cancellationToken);
    }

    private DistributedProjectExecutionLock GetDistributedLock(
        DistributedLockProvider provider,
        string connectionString
    )
    {
        lock (_distributedLockSync)
        {
            if (
                _distributedLock == null
                || _distributedProvider != provider
                || !string.Equals(_distributedConnectionString, connectionString, StringComparison.Ordinal)
            )
            {
                _distributedProvider = provider;
                _distributedConnectionString = connectionString;
                _distributedLock = new DistributedProjectExecutionLock(_providerFactory(provider, connectionString));
            }

            return _distributedLock;
        }
    }
}
