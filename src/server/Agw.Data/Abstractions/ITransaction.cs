using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Shared.Data.Abstractions
{
    public interface ITransaction
    {
        IDbContextTransaction? GetCurrentTransaction();

        bool HasActiveTransaction { get; }

        Task<IDbContextTransaction> TransactionBeginAsync(CancellationToken cancellationToken = default);

        Task<bool> TransactionCommitAsync(CancellationToken cancellationToken = default);

        void TransactionRollback();

        Task TransactionRollbackAsync(CancellationToken cancellationToken = default);
    }
}
