using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

[SuppressMessage(
    "Usage",
    "EF1001:Internal EF Core API usage.",
    Justification = "The optional SDK pins EF Core within each extension generation."
)]
public sealed class ExtensionMigrationsAssembly : MigrationsAssembly
{
    private readonly ExtensionDatabaseProvider _provider;
    private readonly Type _contextType;
    private IReadOnlyDictionary<string, TypeInfo>? _providerMigrations;
    private ModelSnapshot? _providerSnapshot;
    private bool _snapshotSelected;

    public ExtensionMigrationsAssembly(
        ICurrentDbContext currentContext,
        IDbContextOptions options,
        IMigrationsIdGenerator idGenerator,
        IDiagnosticsLogger<DbLoggerCategory.Migrations> logger,
        IEnumerable<IDatabaseProvider> databaseProviders
    )
        : base(currentContext, options, idGenerator, logger)
    {
        _contextType = currentContext.Context.GetType();
        string? provider = databaseProviders.SingleOrDefault(candidate => candidate.IsConfigured(options))?.Name;
        _provider = provider switch
        {
            "Microsoft.EntityFrameworkCore.Sqlite" => ExtensionDatabaseProvider.Sqlite,
            "Npgsql.EntityFrameworkCore.PostgreSQL" => ExtensionDatabaseProvider.PostgreSql,
            _ => throw new AgwException(ErrorCodes.ExtensionPackageInvalid),
        };
    }

    public override IReadOnlyDictionary<string, TypeInfo> Migrations =>
        _providerMigrations ??= base
            .Migrations.Where(entry => MatchesProvider(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    public override ModelSnapshot? ModelSnapshot
    {
        get
        {
            if (_snapshotSelected)
                return _providerSnapshot;
            var snapshots = Assembly
                .DefinedTypes.Where(type =>
                    !type.IsAbstract
                    && type.IsSubclassOf(typeof(ModelSnapshot))
                    && type.GetCustomAttribute<DbContextAttribute>()?.ContextType == _contextType
                )
                .ToArray();
            var matching = snapshots
                .Where(type => type.GetCustomAttribute<ExtensionMigrationProviderAttribute>()?.Provider == _provider)
                .ToArray();
            if (matching.Length > 1)
                throw new AgwException(
                    ErrorCodes.ExtensionPackageInvalid,
                    "Multiple migration snapshots match the database provider."
                );
            if (matching.Length == 1)
                _providerSnapshot = (ModelSnapshot)Activator.CreateInstance(matching[0].AsType())!;
            else
            {
                var shared = snapshots
                    .Where(type => type.GetCustomAttribute<ExtensionMigrationProviderAttribute>() is null)
                    .ToArray();
                if (shared.Length > 1)
                    throw new AgwException(
                        ErrorCodes.ExtensionPackageInvalid,
                        "Multiple shared migration snapshots were declared."
                    );
                if (shared.Length == 1)
                    _providerSnapshot = (ModelSnapshot)Activator.CreateInstance(shared[0].AsType())!;
            }
            _snapshotSelected = true;
            return _providerSnapshot;
        }
    }

    private bool MatchesProvider(TypeInfo migration) =>
        migration.GetCustomAttribute<ExtensionMigrationProviderAttribute>() is not { } attribute
        || attribute.Provider == _provider;
}
