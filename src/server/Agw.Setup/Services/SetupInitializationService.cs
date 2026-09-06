using Agw.Setup.Contracts;
using Agw.Shared.Contracts.Persistence;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.Identity;

namespace Agw.Setup.Services;

public class SetupInitializationService : ISetupInitializationService
{
    private readonly IInitializationStateStore _stateStore;
    private readonly IDatabaseBootstrapper _databaseBootstrapper;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly AgwDataPaths _paths;

    public SetupInitializationService(
        IInitializationStateStore stateStore,
        IDatabaseBootstrapper databaseBootstrapper,
        IPasswordHasher<object> passwordHasher,
        AgwDataPaths paths
    )
    {
        _stateStore = stateStore;
        _databaseBootstrapper = databaseBootstrapper;
        _passwordHasher = passwordHasher;
        _paths = paths;
    }

    public async Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        var configuration = new SetupConfiguration(
            request.DeploymentMode,
            request.Provider,
            SetupConnectionStringFactory.Create(request, _paths)
        );
        await _databaseBootstrapper
            .InitializeAsync(configuration.Provider, configuration.ConnectionString, cancellationToken)
            .ConfigureAwait(false);
        // Seeding does not recover execution scopes. Configured setup is followed by the Host recovery pass;
        // interactive setup wakes the mode-independent recovery service once initialization is persisted.
        // Do not create a fallback in-memory lock for the newly selected database here.
        var passwordHash = _passwordHasher.HashPassword(new object(), request.AdminPassword);
        await _stateStore.PersistAsync(configuration, passwordHash, cancellationToken);
    }
}
