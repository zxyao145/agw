using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agw.Infrastructure.Data;

/// <summary>
/// 租约使用的时钟：PostgreSQL 取数据库当前时间，避免实例之间的时钟偏差；SQLite 只有单个进程写入，使用应用时钟。
/// The clock used by leases: PostgreSQL reads the database time to avoid clock skew between instances; SQLite has a single writing process and uses the application clock.
/// </summary>
internal static class DatabaseClock
{
    public static async Task<DateTimeOffset> GetUtcNowAsync(
        AgwDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        if (!dbContext.Database.IsNpgsql())
            return timeProvider.GetUtcNow();
        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT now()";
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return reader.GetFieldValue<DateTimeOffset>(0);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
