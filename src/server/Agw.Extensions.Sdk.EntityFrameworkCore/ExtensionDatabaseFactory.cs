using System.Data.Common;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionDatabaseFactory<TContext, TMigrationsAssemblyMarker> : IExtensionDatabaseFactory
    where TContext : ExtensionDbContext
    where TMigrationsAssemblyMarker : class
{
    private static readonly string MigrationsAssembly = typeof(TMigrationsAssemblyMarker).Assembly.GetName().Name!;
    private ExtensionNpgsqlDataSourceLifetime? _npgsqlDataSourceLifetime;

    public void ConfigureServices(IServiceCollection services, ExtensionDatabaseOptions options)
    {
        if (services is null)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }
        ValidateOptions(options);
        services.Replace(
            ServiceDescriptor.Singleton(new ExtensionDatabaseSchemaMetadata(options.Provider, options.TablePrefix))
        );
        services.AddLogging();
        services.Replace(
            ServiceDescriptor.Singleton<IExtensionDatabaseIdentity>(
                new ExtensionDatabaseIdentity(options.ExtensionId, options.GenerationId, options.Epoch)
            )
        );
        if (options.Provider == ExtensionDatabaseProvider.Sqlite)
        {
            services.AddEntityFrameworkSqlite();
            Type providerSqlGenerationHelperType =
                services.Last(descriptor => descriptor.ServiceType == typeof(ISqlGenerationHelper)).ImplementationType
                ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
            services.Replace(
                ServiceDescriptor.Scoped<IMigrationsSqlGenerator, ExtensionSqliteMigrationsSqlGenerator>()
            );
            services.Replace(ServiceDescriptor.Scoped<IHistoryRepository, ExtensionSqliteHistoryRepository>());
            services.Replace(
                ServiceDescriptor.Singleton<ISqlGenerationHelper>(serviceProvider => new ExtensionSqlGenerationHelper(
                    serviceProvider.GetRequiredService<RelationalSqlGenerationHelperDependencies>(),
                    options.TablePrefix,
                    (ISqlGenerationHelper)
                        ActivatorUtilities.CreateInstance(serviceProvider, providerSqlGenerationHelperType)
                ))
            );
        }
        else
        {
            services.AddEntityFrameworkNpgsql();
            services.Replace(
                ServiceDescriptor.Scoped<IMigrationsSqlGenerator, ExtensionNpgsqlMigrationsSqlGenerator>()
            );
        }

        services.AddEntityFrameworkNamingConventions();
        services.Replace(ServiceDescriptor.Scoped<IMigrationsAssembly, ExtensionMigrationsAssembly>());
        services.Replace(ServiceDescriptor.Scoped<IQueryCompiler, ExtensionQueryCompiler>());

        Type providerCacheKeyType =
            services
                .Last(descriptor => descriptor.ServiceType == typeof(ICompiledQueryCacheKeyGenerator))
                .ImplementationType
            ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        services.Replace(
            ServiceDescriptor.Scoped<ICompiledQueryCacheKeyGenerator>(
                serviceProvider => new ExtensionCompiledQueryCacheKeyGenerator(
                    (ICompiledQueryCacheKeyGenerator)
                        ActivatorUtilities.CreateInstance(serviceProvider, providerCacheKeyType),
                    serviceProvider.GetRequiredService<ICurrentDbContext>()
                )
            )
        );
        services.Replace(ServiceDescriptor.Scoped<IMigrationsModelDiffer, ExtensionNoForeignKeyModelDiffer>());
        services.Replace(
            ServiceDescriptor.Singleton<IRelationalTransactionFactory, ExtensionRelationalTransactionFactory>()
        );
        services.Replace(ServiceDescriptor.Singleton<IModelCacheKeyFactory, ExtensionModelCacheKeyFactory>());
        services.AddScoped<ExtensionDatabaseTransactionInterceptor>(
            serviceProvider => new ExtensionDatabaseTransactionInterceptor(
                serviceProvider.GetRequiredService<IExtensionDatabaseIdentity>(),
                options.Provider,
                serviceProvider.GetRequiredService<IExtensionEpochValidator>(),
                serviceProvider.GetRequiredService<IExtensionDatabaseTransactionCoordinator>()
            )
        );
        services.AddScoped<ExtensionDatabaseCommandInterceptor>();
        services.AddSingleton<IInterceptor, ExtensionQueryExpressionInterceptor>();
        services.AddDbContext<TContext>(
            (serviceProvider, dbOptions) =>
                ConfigureContext(
                    dbOptions,
                    serviceProvider,
                    options,
                    npgsqlDataSource: _npgsqlDataSourceLifetime?.DataSource
                ),
            contextLifetime: ServiceLifetime.Scoped,
            optionsLifetime: ServiceLifetime.Scoped
        );
        services.AddSingleton<IExtensionOutboxWorker, ExtensionOutboxDispatcher<TContext>>();
    }

    public IExtensionDatabaseGeneration Create(IServiceProvider services, ExtensionDatabaseOptions options)
    {
        if (services is null)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }
        ValidateOptions(options);
        ExtensionEntityFrameworkMetricsLifetime metricsLifetime = ExtensionEntityFrameworkMetricsLifetime.Create();
        ExtensionNpgsqlDataSourceLifetime? npgsqlDataSourceLifetime = null;
        try
        {
            if (options.Provider == ExtensionDatabaseProvider.PostgreSql)
            {
                npgsqlDataSourceLifetime = ExtensionNpgsqlDataSourceLifetime.Create(options.ConnectionString);
                _npgsqlDataSourceLifetime = npgsqlDataSourceLifetime;
            }

            return new ExtensionDatabaseGeneration<TContext, TMigrationsAssemblyMarker>(
                services,
                options,
                metricsLifetime,
                npgsqlDataSourceLifetime
            );
        }
        catch
        {
            _npgsqlDataSourceLifetime = null;
            npgsqlDataSourceLifetime?.Dispose();
            metricsLifetime.Dispose();
            throw;
        }
    }

    internal static DbContextOptionsBuilder ConfigureContext(
        DbContextOptionsBuilder dbOptions,
        IServiceProvider serviceProvider,
        ExtensionDatabaseOptions options,
        DbConnection? connection = null,
        NpgsqlDataSource? npgsqlDataSource = null
    )
    {
        if (options.Provider == ExtensionDatabaseProvider.Sqlite)
        {
            if (connection is null)
            {
                dbOptions.UseSqlite(
                    options.ConnectionString,
                    provider =>
                    {
                        provider.MigrationsAssembly(MigrationsAssembly);
                        provider.MigrationsHistoryTable($"{options.TablePrefix}__ef_migrations_history");
                    }
                );
            }
            else
            {
                dbOptions.UseSqlite(
                    connection,
                    provider =>
                    {
                        provider.MigrationsAssembly(MigrationsAssembly);
                        provider.MigrationsHistoryTable($"{options.TablePrefix}__ef_migrations_history");
                    }
                );
            }
        }
        else
        {
            if (connection is null)
            {
                if (npgsqlDataSource is null)
                {
                    throw new AgwException(ErrorCodes.ExtensionMigrationFailed);
                }

                dbOptions.UseNpgsql(
                    npgsqlDataSource,
                    provider =>
                    {
                        provider.MigrationsAssembly(MigrationsAssembly);
                        provider.MigrationsHistoryTable($"{options.TablePrefix}__ef_migrations_history");
                    }
                );
            }
            else
            {
                dbOptions.UseNpgsql(
                    connection,
                    provider =>
                    {
                        provider.MigrationsAssembly(MigrationsAssembly);
                        provider.MigrationsHistoryTable($"{options.TablePrefix}__ef_migrations_history");
                    }
                );
            }
        }

        dbOptions
            .UseSnakeCaseNamingConvention()
            .UseInternalServiceProvider(serviceProvider)
            .AddInterceptors(
                serviceProvider.GetRequiredService<ExtensionDatabaseTransactionInterceptor>(),
                serviceProvider.GetRequiredService<ExtensionDatabaseCommandInterceptor>()
            );
        return dbOptions;
    }

    private static void ValidateOptions(ExtensionDatabaseOptions options)
    {
        if (options is null)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }
        if (options.ExtensionId == Guid.Empty || options.GenerationId == Guid.Empty || options.Epoch < 0)
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }

        if (
            !string.Equals(
                options.TablePrefix,
                ExtensionDbContext.GetTablePrefix(options.ExtensionId),
                StringComparison.Ordinal
            )
        )
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid);
        }
    }
}
