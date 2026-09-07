using Agw.Infrastructure.Configuration;
using Agw.Setup.Contracts;
using Agw.Shared.Contracts.Persistence;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Agw.Setup.Services;

public class SetupInitializationService : ISetupInitializationService
{
    private readonly IInitializationStateStore _stateStore;
    private readonly IDatabaseBootstrapper _databaseBootstrapper;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly AgwDataPaths _paths;
    private readonly IOptionsMonitor<DatabaseSettings> _databaseSettings;

    public SetupInitializationService(
        IInitializationStateStore stateStore,
        IDatabaseBootstrapper databaseBootstrapper,
        IPasswordHasher<object> passwordHasher,
        AgwDataPaths paths,
        IOptionsMonitor<DatabaseSettings> databaseSettings
    )
    {
        _stateStore = stateStore;
        _databaseBootstrapper = databaseBootstrapper;
        _passwordHasher = passwordHasher;
        _paths = paths;
        _databaseSettings = databaseSettings;
    }

    public async Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        var settings = _databaseSettings.CurrentValue;
        var connectionString = DatabaseConnectionStringResolver.Resolve(
            settings.Provider,
            settings.ConnectionString,
            _paths
        );
        await _databaseBootstrapper
            .InitializeAsync(settings.Provider, connectionString, cancellationToken)
            .ConfigureAwait(false);
        // Seeding does not recover execution scopes. Configured setup is followed by the Host recovery pass;
        // interactive setup wakes the mode-independent recovery service once initialization is persisted.
        var passwordHash = _passwordHasher.HashPassword(new object(), request.AdminPassword);
        await _stateStore.PersistAsync(passwordHash, cancellationToken);
    }
}
