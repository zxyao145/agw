using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Setup.Contracts;
using Agw.Shared.Runtime;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Identity;

namespace Agw.Setup.Services;

public class SetupInitializationService : ISetupInitializationService
{
    private readonly IInitializationStateStore _stateStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly AgwDataPaths _paths;

    public SetupInitializationService(
        IInitializationStateStore stateStore,
        ILoggerFactory loggerFactory,
        IPasswordHasher<object> passwordHasher,
        AgwDataPaths paths)
    {
        _stateStore = stateStore;
        _loggerFactory = loggerFactory;
        _passwordHasher = passwordHasher;
        _paths = paths;
    }

    public async Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        var dbOptions = new DbContextOptionsBuilder<AgwDbContext>();
        var resolvedRequest = new SetupRequest
        {
            Provider = request.Provider,
            ConnectionString = DatabaseConnectionStringResolver.Resolve(request.Provider, request.ConnectionString, _paths),
            AdminPassword = request.AdminPassword,
            SetupCode = request.SetupCode
        };
        ConfigureDatabaseProvider(dbOptions, resolvedRequest);

        await using var context = new AgwDbContext(dbOptions.Options);
        var seeder = new DbSeeder(context, _loggerFactory.CreateLogger<DbSeeder>());
        await seeder.SeedAsync();

        var passwordHash = _passwordHasher.HashPassword(new object(), request.AdminPassword);
        await _stateStore.PersistAsync(resolvedRequest, passwordHash, cancellationToken);
    }

    private static void ConfigureDatabaseProvider(DbContextOptionsBuilder<AgwDbContext> dbOptions, SetupRequest request)
    {
        var settings = new DatabaseSettings
        {
            Provider = request.Provider,
            ConnectionString = request.ConnectionString
        };

        var provider = settings.Provider.Trim().ToLowerInvariant();

        switch (provider)
        {
            case "postgres":
            case "postgresql":
                dbOptions.UseNpgsql(settings.ConnectionString).UseSnakeCaseNamingConvention();
                break;
            case "mysql":
                dbOptions.UseMySql(settings.ConnectionString, ServerVersion.AutoDetect(settings.ConnectionString))
                    .UseSnakeCaseNamingConvention();
                break;
            default:
                dbOptions.UseSqlite(settings.ConnectionString).UseSnakeCaseNamingConvention();
                break;
        }
    }
}
