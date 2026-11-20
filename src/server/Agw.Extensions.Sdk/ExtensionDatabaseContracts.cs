using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Extensions.Sdk.Database;

public enum ExtensionDatabaseProvider
{
    Sqlite,
    PostgreSql,
}

public sealed record ExtensionDatabaseOptions
{
    public required Guid ExtensionId { get; init; }

    public required Guid GenerationId { get; init; }

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string ReleaseHash { get; init; }

    public required long Epoch { get; init; }

    public required string TablePrefix { get; init; }

    public required ExtensionDatabaseProvider Provider { get; init; }

    public required string ConnectionString { get; init; }
}

public interface IExtensionDatabaseConnection
{
    ExtensionDatabaseProvider Provider { get; }

    string ConnectionString { get; }
}

public interface IExtensionDatabaseIdentity
{
    Guid ExtensionId { get; }

    Guid GenerationId { get; }

    long Epoch { get; }

    string TablePrefix { get; }
}

public interface IExtensionUserContext
{
    string? UserId { get; }

    Guid? InvocationId { get; }
}

public interface IExtensionEpochValidator
{
    ValueTask<bool> IsCurrentAsync(
        Guid extensionId,
        Guid generationId,
        long expectedEpoch,
        Guid? invocationId,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default
    );
}

public interface IExtensionSchemaEpochValidator
{
    ValueTask<bool> IsCurrentAsync(
        Guid extensionId,
        Guid generationId,
        long expectedEpoch,
        string releaseHash,
        bool hasPendingMigrations,
        DbConnection openConnection,
        CancellationToken cancellationToken = default
    );
}

public interface IExtensionDatabaseTransactionCoordinator
{
    ValueTask<IAsyncDisposable> AcquireBusinessAccessAsync(
        Guid extensionId,
        ExtensionDatabaseProvider provider,
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken = default
    );
}

public interface IExtensionDatabaseFactory
{
    void ConfigureServices(IServiceCollection services, ExtensionDatabaseOptions options);

    IExtensionDatabaseGeneration Create(IServiceProvider services, ExtensionDatabaseOptions options);
}

public interface IExtensionDatabaseGeneration : IAsyncDisposable
{
    ValueTask ValidateModelAsync(CancellationToken cancellationToken = default);

    ValueTask<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);

    ValueTask PrepareAsync(DbConnection openConnection, CancellationToken cancellationToken = default);
}
