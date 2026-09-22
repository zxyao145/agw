using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data.Encryption;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Shared.Contracts.Persistence;
using Agw.Shared.Runtime;
using Agw.Skills.Contracts.Registration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Data;

public sealed class DatabaseBootstrapper : IDatabaseBootstrapper
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly AgwDataPaths _paths;
    private readonly IOptionsMonitor<DatabaseSettings> _databaseSettings;
    private readonly IEncryptedDataProtector _encryptedDataProtector;
    private readonly EntityCreatorInterceptor _entityCreatorInterceptor;
    private readonly EntityModifierInterceptor _entityModifierInterceptor;
    private readonly EntitySoftDeleteInterceptor _entitySoftDeleteInterceptor;
    private readonly IReadOnlyList<IAgentSkillRegistration> _skillRegistrations;

    public DatabaseBootstrapper(
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        AgwDataPaths paths,
        IOptionsMonitor<DatabaseSettings> databaseSettings,
        IEncryptedDataProtector encryptedDataProtector,
        EntityCreatorInterceptor entityCreatorInterceptor,
        EntityModifierInterceptor entityModifierInterceptor,
        EntitySoftDeleteInterceptor entitySoftDeleteInterceptor,
        IEnumerable<IAgentSkillRegistration> skillRegistrations
    )
    {
        _loggerFactory = loggerFactory;
        _timeProvider = timeProvider;
        _paths = paths;
        _databaseSettings = databaseSettings;
        _encryptedDataProtector = encryptedDataProtector;
        _entityCreatorInterceptor = entityCreatorInterceptor;
        _entityModifierInterceptor = entityModifierInterceptor;
        _entitySoftDeleteInterceptor = entitySoftDeleteInterceptor;
        _skillRegistrations = skillRegistrations.ToArray();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 与 AgwDbContext 注册使用同一份配置和解析规则，保证初始化的数据库就是运行时连接的数据库。
        // Uses the same settings and resolution as the AgwDbContext registration so the initialized database is the runtime database.
        var settings = _databaseSettings.CurrentValue;
        var connectionString = DatabaseConnectionStringResolver.Resolve(
            settings.Provider,
            settings.ConnectionString,
            _paths
        );
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(options, settings.Provider, connectionString);
        options.AddInterceptors(_entityCreatorInterceptor, _entityModifierInterceptor, _entitySoftDeleteInterceptor);

        await using var context = new AgwDbContext(options.Options, _encryptedDataProtector);
        await context.Database.MigrateAsync(cancellationToken);
        var seeder = new DbSeeder(
            context,
            _loggerFactory.CreateLogger<DbSeeder>(),
            _timeProvider,
            _paths,
            _skillRegistrations
        );
        await seeder.SeedAsync();
    }
}
