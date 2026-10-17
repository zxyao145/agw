using Agw.Infrastructure.Configuration;
using Agw.Jobs.Scheduling.Coordination;
using Agw.Shared.Contracts.Coordination;
using Medallion.Threading;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Jobs;

public sealed class ProjectExecutionLockRouter : IProjectExecutionLock
{
    private readonly IOptionsMonitor<DatabaseSettings> _databaseSettings;
    private readonly IOptionsMonitor<DistributedLockSettings> _settings;
    private readonly InMemoryProjectExecutionLock _inMemoryLock;
    private readonly Func<DistributedLockProvider, string, IDistributedLockProvider> _providerFactory;
    private readonly Lock _distributedLockSync = new();

    private DistributedLockProvider? _distributedProvider;
    private string? _distributedConnectionString;
    private DistributedProjectExecutionLock? _distributedLock;

    public ProjectExecutionLockRouter(
        IOptionsMonitor<DatabaseSettings> databaseSettings,
        IOptionsMonitor<DistributedLockSettings> settings,
        InMemoryProjectExecutionLock inMemoryLock,
        Func<DistributedLockProvider, string, IDistributedLockProvider> providerFactory
    )
    {
        _databaseSettings = databaseSettings;
        _settings = settings;
        _inMemoryLock = inMemoryLock;
        _providerFactory = providerFactory;
    }

    public Task<IApplicationLockLease> AcquireAsync(Guid projectId, CancellationToken cancellationToken) =>
        ResolveLock().AcquireAsync(projectId, cancellationToken);

    public Task<IApplicationLockLease?> TryAcquireAsync(Guid projectId, CancellationToken cancellationToken) =>
        ResolveLock().TryAcquireAsync(projectId, cancellationToken);

    private IProjectExecutionLock ResolveLock()
    {
        var databaseSettings = _databaseSettings.CurrentValue;
        var effectiveSettings = DistributedLockSettingsResolver.Resolve(
            _settings.CurrentValue,
            databaseSettings.Provider,
            databaseSettings.ConnectionString
        );

        return effectiveSettings.Provider == DistributedLockProvider.InMemory
            ? _inMemoryLock
            : GetDistributedLock(effectiveSettings.Provider!.Value, effectiveSettings.ConnectionString!);
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
