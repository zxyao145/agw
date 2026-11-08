using System.Collections.Concurrent;
using System.Data.Common;

namespace Agw.Extensions.Sdk.EntityFrameworkCore;

internal static class ExtensionDatabaseTransactionLeaseStore
{
    private static readonly ConcurrentDictionary<DbTransaction, IAsyncDisposable> AccessLeases = new(
        ReferenceEqualityComparer.Instance
    );
    private static readonly ConcurrentDictionary<DbConnection, IAsyncDisposable> PendingAccessLeases = new(
        ReferenceEqualityComparer.Instance
    );

    public static bool TryAddPending(DbConnection connection, IAsyncDisposable lease) =>
        PendingAccessLeases.TryAdd(connection, lease);

    public static IAsyncDisposable? TakePending(DbConnection connection) =>
        PendingAccessLeases.TryRemove(connection, out var lease) ? lease : null;

    public static void ReleasePending(DbConnection connection)
    {
        if (TakePending(connection) is { } lease)
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static async ValueTask ReleasePendingAsync(DbConnection connection)
    {
        if (TakePending(connection) is { } lease)
        {
            await lease.DisposeAsync();
        }
    }

    public static bool TryAdd(DbTransaction transaction, IAsyncDisposable lease) =>
        AccessLeases.TryAdd(transaction, lease);

    public static void Release(DbTransaction transaction)
    {
        if (AccessLeases.TryRemove(transaction, out var lease))
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static async ValueTask ReleaseAsync(DbTransaction transaction)
    {
        if (AccessLeases.TryRemove(transaction, out var lease))
        {
            await lease.DisposeAsync();
        }
    }
}
