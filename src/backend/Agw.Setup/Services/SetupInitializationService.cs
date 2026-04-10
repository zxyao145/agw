using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Setup.Contracts;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Setup.Services;

public class SetupInitializationService : ISetupInitializationService
{
    private readonly IInitializationStateStore _stateStore;
    private readonly ILoggerFactory _loggerFactory;

    public SetupInitializationService(IInitializationStateStore stateStore, ILoggerFactory loggerFactory)
    {
        _stateStore = stateStore;
        _loggerFactory = loggerFactory;
    }

    public async Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        var dbOptions = new DbContextOptionsBuilder<AgwDbContext>();
        ConfigureDatabaseProvider(dbOptions, request);

        await using var context = new AgwDbContext(dbOptions.Options);
        var seeder = new DbSeeder(context, _loggerFactory.CreateLogger<DbSeeder>());
        await seeder.SeedAsync();

        await _stateStore.PersistAsync(request, cancellationToken);
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
