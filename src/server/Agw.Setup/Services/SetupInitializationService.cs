using Agw.Setup.Contracts;
using Agw.Shared.Contracts.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Agw.Setup.Services;

public class SetupInitializationService : ISetupInitializationService
{
    private readonly IInitializationStateStore _stateStore;
    private readonly IDatabaseBootstrapper _databaseBootstrapper;
    private readonly IPasswordHasher<object> _passwordHasher;

    public SetupInitializationService(
        IInitializationStateStore stateStore,
        IDatabaseBootstrapper databaseBootstrapper,
        IPasswordHasher<object> passwordHasher
    )
    {
        _stateStore = stateStore;
        _databaseBootstrapper = databaseBootstrapper;
        _passwordHasher = passwordHasher;
    }

    public async Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        await _databaseBootstrapper.InitializeAsync(cancellationToken).ConfigureAwait(false);
        // Seeding does not recover execution scopes. Configured setup is followed by the Host recovery pass;
        // interactive setup wakes the mode-independent recovery service once initialization is persisted.
        var passwordHash = _passwordHasher.HashPassword(new object(), request.AdminPassword);
        await _stateStore.PersistAsync(passwordHash, cancellationToken);
    }
}
