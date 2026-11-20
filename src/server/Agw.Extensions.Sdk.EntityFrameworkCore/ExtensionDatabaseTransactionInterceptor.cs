using System.Data.Common;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

public sealed class ExtensionDatabaseTransactionInterceptor : DbTransactionInterceptor
{
    private readonly IExtensionDatabaseIdentity _identity;
    private readonly ExtensionDatabaseProvider _provider;
    private readonly IExtensionEpochValidator _epochValidator;
    private readonly IExtensionDatabaseTransactionCoordinator _transactionCoordinator;
    private readonly AsyncLocal<int> _migrationDepth = new();
    private readonly AsyncLocal<int> _readDepth = new();

    public ExtensionDatabaseTransactionInterceptor(
        IExtensionDatabaseIdentity identity,
        ExtensionDatabaseProvider provider,
        IExtensionEpochValidator epochValidator,
        IExtensionDatabaseTransactionCoordinator transactionCoordinator
    )
    {
        _identity = identity;
        _provider = provider;
        _epochValidator = epochValidator;
        _transactionCoordinator = transactionCoordinator;
    }

    public IDisposable EnterMigration()
    {
        _migrationDepth.Value++;
        return new MigrationScope(this);
    }

    internal bool IsMigration => _migrationDepth.Value > 0;

    internal IDisposable EnterReadTransaction()
    {
        _readDepth.Value++;
        return new ReadScope(this);
    }

    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result
    )
    {
        if (!ShouldAcquireSqliteAccess(connection, result))
        {
            return GetTransactionStartingResult(connection, eventData, result);
        }

        AcquireSqliteBusinessAccessAsync(connection, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try
        {
            return GetTransactionStartingResult(connection, eventData, result);
        }
        catch
        {
            ExtensionDatabaseTransactionLeaseStore.ReleasePending(connection);
            throw;
        }
    }

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ShouldAcquireSqliteAccess(connection, result))
        {
            return GetTransactionStartingResult(connection, eventData, result);
        }

        await AcquireSqliteBusinessAccessAsync(connection, cancellationToken);
        try
        {
            return GetTransactionStartingResult(connection, eventData, result);
        }
        catch
        {
            await ExtensionDatabaseTransactionLeaseStore.ReleasePendingAsync(connection);
            throw;
        }
    }

    public override DbTransaction TransactionUsed(
        DbConnection connection,
        TransactionEventData eventData,
        DbTransaction result
    ) =>
        throw new AgwException(
            ErrorCodes.ExtensionPackageInvalid,
            "External extension database transactions are unsupported."
        );

    public override ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection,
        TransactionEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default
    ) =>
        throw new AgwException(
            ErrorCodes.ExtensionPackageInvalid,
            "External extension database transactions are unsupported."
        );

    public override DbTransaction TransactionStarted(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result
    )
    {
        if (!IsMigration)
        {
            IAsyncDisposable? lease =
                _provider == ExtensionDatabaseProvider.Sqlite
                    ? ExtensionDatabaseTransactionLeaseStore.TakePending(connection)
                        ?? throw new AgwException(ErrorCodes.ExtensionUnavailable)
                    : null;
            AcquireBusinessAccessAsync(
                    connection,
                    result,
                    RequireContext(eventData.Context),
                    CancellationToken.None,
                    lease
                )
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsMigration)
        {
            IAsyncDisposable? lease =
                _provider == ExtensionDatabaseProvider.Sqlite
                    ? ExtensionDatabaseTransactionLeaseStore.TakePending(connection)
                        ?? throw new AgwException(ErrorCodes.ExtensionUnavailable)
                    : null;
            await AcquireBusinessAccessAsync(
                connection,
                result,
                RequireContext(eventData.Context),
                cancellationToken,
                lease
            );
        }

        return result;
    }

    public override InterceptionResult TransactionCommitting(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result
    )
    {
        if (!IsMigration)
        {
            ValidateCurrentAsync(transaction, RequireContext(eventData.Context), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        return result;
    }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsMigration)
        {
            await ValidateCurrentAsync(transaction, RequireContext(eventData.Context), cancellationToken);
        }

        return result;
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        ReleaseAccess(transaction);
    }

    public override async Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        await ReleaseAccessAsync(transaction);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        ReleaseAccess(transaction);
    }

    public override async Task TransactionRolledBackAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        await ReleaseAccessAsync(transaction);
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        if (GetConnection(transaction, eventData.Context) is { } connection)
        {
            ExtensionDatabaseTransactionLeaseStore.ReleasePending(connection);
        }
        if (transaction.Connection is null)
        {
            ReleaseAccess(transaction);
        }
    }

    public override async Task TransactionFailedAsync(
        DbTransaction transaction,
        TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        if (GetConnection(transaction, eventData.Context) is { } connection)
        {
            await ExtensionDatabaseTransactionLeaseStore.ReleasePendingAsync(connection);
        }
        if (transaction.Connection is null)
        {
            await ReleaseAccessAsync(transaction);
        }
    }

    internal async ValueTask ValidateCurrentAsync(
        DbTransaction transaction,
        ExtensionDbContext context,
        CancellationToken cancellationToken
    )
    {
        var connection = transaction.Connection;
        if (
            connection is null
            || !await _epochValidator.IsCurrentAsync(
                _identity.ExtensionId,
                _identity.GenerationId,
                _identity.Epoch,
                context.InvocationId,
                connection,
                transaction,
                cancellationToken
            )
        )
        {
            throw new AgwException(ErrorCodes.ExtensionUnavailable);
        }
    }

    private async ValueTask AcquireBusinessAccessAsync(
        DbConnection connection,
        DbTransaction transaction,
        ExtensionDbContext context,
        CancellationToken cancellationToken,
        IAsyncDisposable? preAcquiredLease = null
    )
    {
        IAsyncDisposable? lease = preAcquiredLease;
        try
        {
            lease ??= await _transactionCoordinator.AcquireBusinessAccessAsync(
                _identity.ExtensionId,
                _provider,
                connection,
                transaction,
                cancellationToken
            );
            await ValidateCurrentAsync(transaction, context, cancellationToken);
            if (!ExtensionDatabaseTransactionLeaseStore.TryAdd(transaction, lease))
            {
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }
        }
        catch
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                if (lease is not null)
                    await lease.DisposeAsync();
            }
            throw;
        }
    }

    private void ReleaseAccess(DbTransaction transaction)
    {
        ExtensionDatabaseTransactionLeaseStore.Release(transaction);
    }

    private ValueTask ReleaseAccessAsync(DbTransaction transaction) =>
        ExtensionDatabaseTransactionLeaseStore.ReleaseAsync(transaction);

    private async ValueTask AcquireSqliteBusinessAccessAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        if (connection is not SqliteConnection)
        {
            throw new AgwException(ErrorCodes.ExtensionMigrationFailed);
        }

        IAsyncDisposable lease = await _transactionCoordinator.AcquireBusinessAccessAsync(
            _identity.ExtensionId,
            _provider,
            connection,
            null,
            cancellationToken
        );
        if (ExtensionDatabaseTransactionLeaseStore.TryAddPending(connection, lease))
        {
            return;
        }

        await lease.DisposeAsync();
        throw new AgwException(ErrorCodes.ExtensionUnavailable);
    }

    private bool ShouldAcquireSqliteAccess(DbConnection connection, InterceptionResult<DbTransaction> result) =>
        !result.HasResult && !IsMigration && _provider == ExtensionDatabaseProvider.Sqlite;

    private InterceptionResult<DbTransaction> GetTransactionStartingResult(
        DbConnection connection,
        TransactionStartingEventData eventData,
        InterceptionResult<DbTransaction> result
    )
    {
        if (
            result.HasResult
            || IsMigration
            || _provider != ExtensionDatabaseProvider.Sqlite
            || connection is not SqliteConnection sqlite
        )
        {
            return result;
        }

        return InterceptionResult<DbTransaction>.SuppressWithResult(
            sqlite.BeginTransaction(eventData.IsolationLevel, deferred: _readDepth.Value > 0)
        );
    }

    private static DbConnection? GetConnection(
        DbTransaction transaction,
        Microsoft.EntityFrameworkCore.DbContext? context
    ) => transaction.Connection ?? (context as ExtensionDbContext)?.Database.GetDbConnection();

    private static ExtensionDbContext RequireContext(Microsoft.EntityFrameworkCore.DbContext? context) =>
        context as ExtensionDbContext ?? throw new AgwException(ErrorCodes.ExtensionPackageInvalid);

    private sealed class MigrationScope : IDisposable
    {
        private readonly ExtensionDatabaseTransactionInterceptor _interceptor;
        private bool _disposed;

        public MigrationScope(ExtensionDatabaseTransactionInterceptor interceptor)
        {
            _interceptor = interceptor;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _interceptor._migrationDepth.Value--;
            _disposed = true;
        }
    }

    private sealed class ReadScope(ExtensionDatabaseTransactionInterceptor interceptor) : IDisposable
    {
        public void Dispose() => interceptor._readDepth.Value--;
    }
}
