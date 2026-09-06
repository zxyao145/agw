using Agw.Infrastructure.Data.Encryption;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Persistence;
using Agw.Shared.Runtime;
using Agw.Skills.Contracts.Registration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Agw.Infrastructure.Data;

public sealed class DatabaseBootstrapper : IDatabaseBootstrapper
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly AgwDataPaths _paths;
    private readonly IEncryptedDataProtector _encryptedDataProtector;
    private readonly EntityCreatorInterceptor _entityCreatorInterceptor;
    private readonly EntityModifierInterceptor _entityModifierInterceptor;
    private readonly EntitySoftDeleteInterceptor _entitySoftDeleteInterceptor;
    private readonly IReadOnlyList<IAgentSkillRegistration> _skillRegistrations;

    public DatabaseBootstrapper(
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        AgwDataPaths paths,
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
        _encryptedDataProtector = encryptedDataProtector;
        _entityCreatorInterceptor = entityCreatorInterceptor;
        _entityModifierInterceptor = entityModifierInterceptor;
        _entitySoftDeleteInterceptor = entitySoftDeleteInterceptor;
        _skillRegistrations = skillRegistrations.ToArray();
    }

    public async Task InitializeAsync(
        DatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default
    )
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        AgwDbContextOptionsConfigurator.Configure(options, provider, connectionString);
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
