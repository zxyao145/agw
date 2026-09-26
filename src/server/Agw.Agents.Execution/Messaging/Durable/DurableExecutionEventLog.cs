using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Turns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agw.Agents.Execution.Messaging.Durable;

/// <summary>
/// Turn 事件的读取与投影发布。PostgreSQL 是事件的可靠来源；配置 Redis 时，读取优先使用投影中与游标连续的部分，其余从 PostgreSQL 补齐。
/// Reading and projection publishing of turn events. PostgreSQL is the reliable source; with Redis configured, reads prefer the projection's part contiguous with the cursor and fill the rest from PostgreSQL.
/// </summary>
internal sealed class DurableExecutionEventLog
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DurableExecutionEventLog> _logger;
    private readonly RedisExecutionEventProjection? _projection;
    private readonly int _readBatchSize;

    public DurableExecutionEventLog(
        IServiceScopeFactory scopeFactory,
        IOptions<ExecutionRuntimeOptions> options,
        ILogger<DurableExecutionEventLog> logger,
        RedisExecutionEventProjection? projection = null
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _projection = projection;
        _readBatchSize = options.Value.Distributed.EventStream.ReadBatchSize;
    }

    public async Task<IReadOnlyList<TurnBroadcastEntry>> ReadAsync(
        Guid turnId,
        long afterSequence,
        CancellationToken cancellationToken
    )
    {
        if (_projection != null)
        {
            try
            {
                var projected = TakeContiguous(
                    await _projection.ReadAsync(turnId, afterSequence, cancellationToken).ConfigureAwait(false),
                    afterSequence
                );
                if (projected.Count > 0)
                    return projected;
            }
            catch (RedisException exception)
            {
                // 投影只改善读取延迟；不可用时直接读 PostgreSQL。
                // The projection only improves read latency; when unavailable, PostgreSQL is read directly.
                _logger.LogWarning(exception, "The Redis projection of turn {TurnId} is unavailable.", turnId);
            }
        }

        return await ReadCommittedAsync(turnId, afterSequence, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 提交后把事件写入投影；投影落后于本批时先从 PostgreSQL 补齐缺少的部分。
    /// Writes events to the projection after commit; when the projection lags behind this batch, the missing part is filled from PostgreSQL first.
    /// </summary>
    public async Task PublishAsync(
        Guid turnId,
        IReadOnlyList<TurnBroadcastEntry> committed,
        CancellationToken cancellationToken
    )
    {
        if (_projection == null || committed.Count == 0)
            return;
        try
        {
            var projected = await _projection.GetLastSequenceAsync(turnId, cancellationToken).ConfigureAwait(false);
            var last = projected;
            var entries = new List<TurnBroadcastEntry>();
            var first = committed.Min(entry => entry.Sequence);
            while (last + 1 < first)
            {
                var missing = await ReadCommittedAsync(turnId, last, cancellationToken).ConfigureAwait(false);
                var contiguous = TakeContiguous(missing.Where(entry => entry.Sequence < first).ToArray(), last);
                if (contiguous.Count == 0)
                    break;
                entries.AddRange(contiguous);
                last = contiguous[^1].Sequence;
            }
            entries.AddRange(committed);
            await _projection
                .PublishAsync(turnId, entries, lastSequence: projected, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            // 发布失败不影响已提交的事件；下一次发布或读取从 PostgreSQL 补齐。
            // A failed publication leaves committed events intact; the next publication or read fills them from PostgreSQL.
            _logger.LogWarning(exception, "Failed to project committed events of turn {TurnId} to Redis.", turnId);
        }
    }

    private async Task<IReadOnlyList<TurnBroadcastEntry>> ReadCommittedAsync(
        Guid turnId,
        long afterSequence,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await DurableExecutionEvents
            .ReadAsync(
                scope.ServiceProvider.GetRequiredService<IAgentsDbContext>(),
                turnId,
                afterSequence,
                _readBatchSize,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<TurnBroadcastEntry> TakeContiguous(
        IReadOnlyList<TurnBroadcastEntry> entries,
        long afterSequence
    )
    {
        var result = new List<TurnBroadcastEntry>();
        var expected = afterSequence + 1;
        foreach (var entry in entries.OrderBy(item => item.Sequence))
        {
            if (entry.Sequence < expected)
                continue;
            if (entry.Sequence != expected)
                break;
            result.Add(entry);
            expected++;
        }
        return result;
    }
}
