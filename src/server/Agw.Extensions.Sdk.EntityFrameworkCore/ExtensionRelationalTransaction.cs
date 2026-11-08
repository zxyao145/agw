using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal sealed class ExtensionRelationalTransaction : RelationalTransaction
{
    private readonly DbTransaction _dbTransaction;
    private readonly bool _transactionOwned;

    public ExtensionRelationalTransaction(
        IRelationalConnection connection,
        DbTransaction transaction,
        Guid transactionId,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger,
        bool transactionOwned,
        ISqlGenerationHelper sqlGenerationHelper
    )
        : base(connection, transaction, transactionId, logger, transactionOwned, sqlGenerationHelper)
    {
        _dbTransaction = transaction;
        _transactionOwned = transactionOwned;
    }

    public override void Dispose()
    {
        try
        {
            base.Dispose();
        }
        finally
        {
            if (_transactionOwned)
                ExtensionDatabaseTransactionLeaseStore.Release(_dbTransaction);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            if (_transactionOwned)
                await ExtensionDatabaseTransactionLeaseStore.ReleaseAsync(_dbTransaction);
        }
    }
}
