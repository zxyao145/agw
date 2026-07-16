using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Setup.Contracts;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Setup.Services;

public class SetupInitializationService : ISetupInitializationService
{
    private readonly IInitializationStateStore _stateStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly AgwDataPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly IEncryptedDataProtector _encryptedDataProtector;

    public SetupInitializationService(
        IInitializationStateStore stateStore,
        ILoggerFactory loggerFactory,
        IPasswordHasher<object> passwordHasher,
        AgwDataPaths paths,
        TimeProvider timeProvider,
        IEncryptedDataProtector encryptedDataProtector)
    {
        _stateStore = stateStore;
        _loggerFactory = loggerFactory;
        _passwordHasher = passwordHasher;
        _paths = paths;
        _timeProvider = timeProvider;
        _encryptedDataProtector = encryptedDataProtector;
    }

    public async Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        var dbOptions = new DbContextOptionsBuilder<AgwDbContext>();
        var resolvedRequest = new SetupRequest
        {
            Provider = request.Provider,
            ConnectionString = DatabaseConnectionStringResolver.Resolve(
                request.Provider,
                request.ConnectionString,
                _paths),
            AdminPassword = request.AdminPassword,
            SetupCode = request.SetupCode
        };
        ConfigureDatabaseProvider(dbOptions, resolvedRequest);

        await using var context = new AgwDbContext(dbOptions.Options, _encryptedDataProtector);
        var seeder = new DbSeeder(context, _loggerFactory.CreateLogger<DbSeeder>(), _timeProvider, _paths);
        await seeder.SeedAsync();

        var passwordHash = _passwordHasher.HashPassword(new object(), request.AdminPassword);
        await _stateStore.PersistAsync(resolvedRequest, passwordHash, cancellationToken);
    }

    private static void ConfigureDatabaseProvider(DbContextOptionsBuilder<AgwDbContext> dbOptions, SetupRequest request)
    {
        if (request.Provider == DatabaseProvider.Postgres)
        {
            dbOptions.UseNpgsql(request.ConnectionString).UseSnakeCaseNamingConvention();
            return;
        }

        dbOptions.UseSqlite(request.ConnectionString).UseSnakeCaseNamingConvention();
    }
}
