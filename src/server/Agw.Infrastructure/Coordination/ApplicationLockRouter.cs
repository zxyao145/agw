using Agw.Infrastructure.Configuration;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Runtime;
using Medallion.Threading;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Coordination;

public sealed class ApplicationLockRouter : IApplicationLock
{
    private readonly IServerInitializationState _serverInitializationState;
    private readonly IOptionsMonitor<DistributedLockSettings> _settings;
    private readonly InMemoryApplicationLock _inMemoryLock;
    private readonly Func<DistributedLockProvider, string, IDistributedLockProvider> _providerFactory;
    private readonly object _distributedLockSync = new();

    private DistributedLockProvider? _distributedProvider;
    private string? _distributedConnectionString;
    private IDistributedLockProvider? _distributedLockProvider;

    public ApplicationLockRouter(
        IServerInitializationState serverInitializationState,
        IOptionsMonitor<DistributedLockSettings> settings,
        InMemoryApplicationLock inMemoryLock,
        Func<DistributedLockProvider, string, IDistributedLockProvider> providerFactory
    )
    {
        _serverInitializationState = serverInitializationState;
        _settings = settings;
        _inMemoryLock = inMemoryLock;
        _providerFactory = providerFactory;
    }

    public async Task<IAsyncDisposable> AcquireAsync(string resourceName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var effectiveSettings = DistributedLockSettingsResolver.Resolve(
            _settings.CurrentValue,
            _serverInitializationState.DatabaseProvider,
            _serverInitializationState.DatabaseConnectionString
        );
        if (effectiveSettings.Provider == DistributedLockProvider.InMemory)
        {
            return await _inMemoryLock.AcquireAsync(resourceName, cancellationToken).ConfigureAwait(false);
        }

        return await GetDistributedLockProvider(effectiveSettings.Provider!.Value, effectiveSettings.ConnectionString!)
            .AcquireLockAsync($"agw:application:{resourceName}", cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private IDistributedLockProvider GetDistributedLockProvider(
        DistributedLockProvider provider,
        string connectionString
    )
    {
        lock (_distributedLockSync)
        {
            if (
                _distributedLockProvider == null
                || _distributedProvider != provider
                || !string.Equals(_distributedConnectionString, connectionString, StringComparison.Ordinal)
            )
            {
                _distributedProvider = provider;
                _distributedConnectionString = connectionString;
                _distributedLockProvider = _providerFactory(provider, connectionString);
            }

            return _distributedLockProvider;
        }
    }
}
