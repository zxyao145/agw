using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Agents;

/// <summary>
/// 用一条 UPDATE ... RETURNING 递增并读取事件序号；PostgreSQL 与 SQLite 都支持这一语法，语句沿用调用方的当前事务。
/// Increments and reads the event sequence with one UPDATE ... RETURNING, supported by both PostgreSQL and SQLite; the statement uses the caller's current transaction.
/// </summary>
public sealed class DurableExecutionEventSequence : IDurableExecutionEventSequence
{
    private readonly AgwDbContext _dbContext;

    public DurableExecutionEventSequence(AgwDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<long> ReserveAsync(Guid executionId, int count, CancellationToken cancellationToken)
    {
        var values = await _dbContext
            .Database.SqlQuery<long>(
                $"""
                UPDATE durable_execution
                SET last_event_sequence = last_event_sequence + {count}
                WHERE id = {executionId}
                RETURNING last_event_sequence AS "Value"
                """
            )
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return values.Count == 1
            ? values[0]
            : throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Durable execution '{executionId}' does not exist."
            );
    }
}
