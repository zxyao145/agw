using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Shared.Data.Abstractions;

#region EFContext 不使用Identity

/// <summary>
/// EF DbContext
/// </summary>
public partial class EFContext : DbContext, IUnitOfWork, ITransaction
{
    public EFContext()
        : base() { }

    public EFContext(DbContextOptions options)
        : base(options) { }

    #region ITransaction

    private IDbContextTransaction? _currentTransaction;

    public IDbContextTransaction? GetCurrentTransaction() => _currentTransaction;

    public bool HasActiveTransaction => _currentTransaction != null;

    public virtual async Task<IDbContextTransaction> TransactionBeginAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (_currentTransaction != null)
        {
            return _currentTransaction;
        }

        _currentTransaction = await Database.BeginTransactionAsync(cancellationToken);
        return _currentTransaction;
    }

    public virtual async Task<bool> TransactionCommitAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction == null)
        {
            return false;
        }

        IDbContextTransaction transaction = _currentTransaction;

        try
        {
            await SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await TransactionRollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (_currentTransaction != null)
            {
                _currentTransaction.Dispose();
                _currentTransaction = null;
            }
        }
    }

    public void TransactionRollback()
    {
        try
        {
            _currentTransaction?.Rollback();
        }
        finally
        {
            if (HasActiveTransaction)
            {
                _currentTransaction!.Dispose();
                _currentTransaction = null;
            }
        }
    }

    public async Task TransactionRollbackAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_currentTransaction != null)
            {
                await _currentTransaction.RollbackAsync(cancellationToken);
            }
        }
        finally
        {
            if (_currentTransaction != null)
            {
                await _currentTransaction.DisposeAsync();
                _currentTransaction = null;
            }
        }
    }

    #endregion
}

#endregion
