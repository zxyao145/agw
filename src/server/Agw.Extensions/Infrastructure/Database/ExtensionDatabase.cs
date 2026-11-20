using System.Data.Common;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Runtime;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Infrastructure.Database;

internal sealed class ExtensionDatabase : IExtensionDatabase
{
    private readonly IExtensionGenerationAccessor _generations;
    private readonly IExtensionDatabaseConnection _connection;
    private readonly ExtensionDatabaseLockCoordinator _locks;
    private readonly ILogger<ExtensionDatabase> _logger;

    public ExtensionDatabase(
        IExtensionGenerationAccessor generations,
        IExtensionDatabaseConnection connection,
        ExtensionDatabaseLockCoordinator locks,
        ILogger<ExtensionDatabase> logger
    )
    {
        _generations = generations;
        _connection = connection;
        _locks = locks;
        _logger = logger;
    }

    public async Task ValidateModelAsync(
        ExtensionGenerationId generationId,
        CancellationToken cancellationToken = default
    )
    {
        IExtensionDatabaseGeneration generation = GetGeneration(generationId);
        await generation.ValidateModelAsync(cancellationToken);
    }

    public async Task PrepareAsync(ExtensionGenerationId generationId, CancellationToken cancellationToken = default)
    {
        IExtensionDatabaseGeneration generation = GetGeneration(generationId);
        await using DbConnection connection = await generation.CreateConnectionAsync(cancellationToken);
        await connection.OpenAsync(cancellationToken);
        IAsyncDisposable migrationLock = await _locks.AcquireMigrationAccessAsync(
            generationId.ExtensionId,
            _connection.Provider,
            connection,
            cancellationToken
        );

        try
        {
            await generation.PrepareAsync(connection, cancellationToken);
        }
        catch
        {
            try
            {
                await migrationLock.DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Extension {ExtensionId} migration lock cleanup failed: {Failure}",
                    generationId.ExtensionId,
                    exception.ToString()
                );
            }
            throw;
        }
        await migrationLock.DisposeAsync();
    }

    private IExtensionDatabaseGeneration GetGeneration(ExtensionGenerationId generationId) =>
        _generations.GetDatabaseGeneration(generationId) ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);
}
