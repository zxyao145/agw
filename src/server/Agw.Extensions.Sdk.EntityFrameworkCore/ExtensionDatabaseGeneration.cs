using System.Data.Common;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionDatabaseGeneration<TContext, TMigrationsAssemblyMarker> : IExtensionDatabaseGeneration
    where TContext : ExtensionDbContext
    where TMigrationsAssemblyMarker : class
{
    private readonly ExtensionDatabaseOptions _options;
    private readonly ExtensionDatabaseSchemaMetadata _schemaMetadata;
    private ExtensionEntityFrameworkMetricsLifetime? _metricsLifetime;
    private ExtensionNpgsqlDataSourceLifetime? _npgsqlDataSourceLifetime;
    private IServiceProvider? _services;

    public ExtensionDatabaseGeneration(
        IServiceProvider services,
        ExtensionDatabaseOptions options,
        ExtensionEntityFrameworkMetricsLifetime metricsLifetime,
        ExtensionNpgsqlDataSourceLifetime? npgsqlDataSourceLifetime
    )
    {
        _services = services;
        _options = options;
        _schemaMetadata = new ExtensionDatabaseSchemaMetadata(options.Provider, options.TablePrefix);
        _metricsLifetime = metricsLifetime;
        _npgsqlDataSourceLifetime = npgsqlDataSourceLifetime;
    }

    public async ValueTask ValidateModelAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        ValidateContext(context);
    }

    public ValueTask<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        DbConnection connection = _options.Provider switch
        {
            ExtensionDatabaseProvider.Sqlite => new SqliteConnection(_options.ConnectionString),
            ExtensionDatabaseProvider.PostgreSql => _npgsqlDataSourceLifetime?.DataSource.CreateConnection()
                ?? throw new AgwException(ErrorCodes.ExtensionMigrationFailed),
            _ => throw new AgwException(ErrorCodes.UnsupportedDatabaseProvider),
        };
        return ValueTask.FromResult(connection);
    }

    public async ValueTask PrepareAsync(DbConnection openConnection, CancellationToken cancellationToken = default)
    {
        if (openConnection is null)
        {
            throw new AgwException(ErrorCodes.ExtensionMigrationFailed);
        }
        if (openConnection.State != System.Data.ConnectionState.Open)
        {
            throw new AgwException(ErrorCodes.ExtensionMigrationFailed);
        }

        await using var scope = Services.CreateAsyncScope();
        var serviceProvider = scope.ServiceProvider;
        var configuredOptions = serviceProvider.GetRequiredService<DbContextOptions<TContext>>();
        var contextOptions = new DbContextOptionsBuilder<TContext>(configuredOptions);
        ExtensionDatabaseFactory<TContext, TMigrationsAssemblyMarker>.ConfigureContext(
            contextOptions,
            serviceProvider,
            _options,
            openConnection
        );
        await using var context = ActivatorUtilities.CreateInstance<TContext>(serviceProvider, contextOptions.Options);
        if (!ReferenceEquals(context.Database.GetDbConnection(), openConnection))
        {
            throw new AgwException(
                ErrorCodes.ExtensionMigrationFailed,
                "The extension migration context must use the connection holding the migration lock."
            );
        }
        ValidateContext(context);

        using var migrationScope = serviceProvider
            .GetRequiredService<ExtensionDatabaseTransactionInterceptor>()
            .EnterMigration();
        bool hasPendingMigrations = await ValidateAppliedMigrationsArePackagedAsync(context, cancellationToken);
        IExtensionSchemaEpochValidator schemaEpochValidator =
            serviceProvider.GetRequiredService<IExtensionSchemaEpochValidator>();
        if (
            !await schemaEpochValidator.IsCurrentAsync(
                _options.ExtensionId,
                _options.GenerationId,
                _options.Epoch,
                _options.ReleaseHash,
                hasPendingMigrations,
                openConnection,
                cancellationToken
            )
        )
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }

        if (_options.Provider == ExtensionDatabaseProvider.Sqlite)
        {
            var history =
                context.GetService<IHistoryRepository>() as ExtensionSqliteHistoryRepository
                ?? throw new AgwException(ErrorCodes.ExtensionMigrationFailed);
            await history.ClearAbandonedLockAsync(openConnection, cancellationToken);
        }
        await context.Database.MigrateAsync(cancellationToken);
        if (
            !await schemaEpochValidator.IsCurrentAsync(
                _options.ExtensionId,
                _options.GenerationId,
                _options.Epoch,
                _options.ReleaseHash,
                false,
                openConnection,
                cancellationToken
            )
        )
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ExtensionNpgsqlDataSourceLifetime? npgsqlDataSourceLifetime = Interlocked.Exchange(
            ref _npgsqlDataSourceLifetime,
            null
        );
        try
        {
            if (npgsqlDataSourceLifetime is not null)
            {
                await npgsqlDataSourceLifetime.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _metricsLifetime, null)?.Dispose();
            _services = null;
        }
    }

    private IServiceProvider Services => _services ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);

    private void ValidateContext(TContext context)
    {
        var identity = context.ExtensionDatabaseIdentity;
        if (
            identity.ExtensionId != _options.ExtensionId
            || identity.GenerationId != _options.GenerationId
            || identity.Epoch != _options.Epoch
            || !string.Equals(identity.TablePrefix, _options.TablePrefix, StringComparison.Ordinal)
        )
        {
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "The extension database context has an invalid identity."
            );
        }

        ExtensionDatabaseModelValidator.Validate(
            context.GetService<IDesignTimeModel>().Model,
            _schemaMetadata,
            context.GetService<IRelationalTypeMappingSource>()
        );
        ExtensionMigrationValidator.Validate(context, _schemaMetadata);
    }

    private static async Task<bool> ValidateAppliedMigrationsArePackagedAsync(
        TContext context,
        CancellationToken cancellationToken
    )
    {
        var packagedMigrations = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var appliedMigrations = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        if (appliedMigrations.Any(migration => !packagedMigrations.Contains(migration)))
        {
            throw new AgwException(
                ErrorCodes.ExtensionMigrationFailed,
                "The retained extension schema includes a migration absent from this release."
            );
        }

        return packagedMigrations.Any(migration => !appliedMigrations.Contains(migration, StringComparer.Ordinal));
    }
}
