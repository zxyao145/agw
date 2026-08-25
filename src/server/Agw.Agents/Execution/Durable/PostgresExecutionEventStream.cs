using System.Data.Common;
using System.Globalization;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 使用 PostgreSQL append-only 表保存 execution 消息，并按确定性 cursor 回放。
/// 消息表不参与 execution 状态机，因此回放故障不会改变执行结果。
/// </summary>
internal sealed class PostgresExecutionEventStream : IExecutionEventStream
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly int _readBatchSize;

    /// <summary>
    /// 创建按操作获取独立 DbContext scope 的 PostgreSQL 消息回放实现。
    /// </summary>
    public PostgresExecutionEventStream(IServiceScopeFactory scopeFactory, IOptions<ExecutionRuntimeOptions> options)
    {
        _scopeFactory = scopeFactory;
        _readBatchSize = options.Value.Distributed.EventStream.ReadBatchSize;
    }

    /// <summary>
    /// 在 execution、segment 和 sequence 的唯一位置幂等追加一条加密消息。
    /// </summary>
    public async ValueTask AppendAsync(
        Guid executionId,
        int segmentIndex,
        int sequence,
        AgwMessage message,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(message);
        if (segmentIndex < 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "segmentIndex must be non-negative.");
        }
        if (sequence < 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "sequence must be non-negative.");
        }

        var payload = JsonUtil.Serialize(message);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
            dbContext
                .Set<DurableExecutionEventRecord>()
                .Add(
                    new DurableExecutionEventRecord
                    {
                        Id = Guid.CreateVersion7(),
                        ExecutionId = executionId,
                        SegmentIndex = segmentIndex,
                        Sequence = sequence,
                        PayloadJson = payload,
                    }
                );
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            if (
                await ExistsAtPositionAsync(executionId, segmentIndex, sequence, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                return;
            }

            throw CreateUnavailableException("append", exception);
        }
        catch (Exception exception) when (IsDatabaseFailure(exception))
        {
            throw CreateUnavailableException("append", exception);
        }
    }

    /// <summary>
    /// 按 cursor 之后的确定性位置读取一批消息，并补充 executionId 与最新 cursor。
    /// </summary>
    public async Task<IReadOnlyList<ExecutionStreamEntry>> ReadAsync(
        Guid executionId,
        string? afterCursor,
        CancellationToken cancellationToken
    )
    {
        var cursor = ParseCursor(afterCursor);
        IReadOnlyList<DurableExecutionEventRecord> records;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
            var query = dbContext
                .Set<DurableExecutionEventRecord>()
                .AsNoTracking()
                .Where(item => item.ExecutionId == executionId);
            if (cursor.SegmentIndex >= 0)
            {
                query = cursor.Sequence.HasValue
                    ? query.Where(item =>
                        item.SegmentIndex > cursor.SegmentIndex
                        || (item.SegmentIndex == cursor.SegmentIndex && item.Sequence > cursor.Sequence.Value)
                    )
                    : query.Where(item => item.SegmentIndex > cursor.SegmentIndex);
            }

            records = await query
                .OrderBy(item => item.SegmentIndex)
                .ThenBy(item => item.Sequence)
                .Take(_readBatchSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsDatabaseFailure(exception))
        {
            throw CreateUnavailableException("read", exception);
        }

        var result = new List<ExecutionStreamEntry>(records.Count);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var streamCursor = CreateCursor(record.SegmentIndex, record.Sequence);
            var message =
                JsonUtil.Deserialize<AgwMessage>(record.PayloadJson)
                ?? throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    $"Execution stream entry '{streamCursor}' contains an invalid message."
                );
            var properties =
                message.AdditionalProperties == null
                    ? new AdditionalPropertiesDictionary()
                    : new AdditionalPropertiesDictionary(message.AdditionalProperties);
            properties["executionId"] = executionId.ToString("D");
            properties["streamCursor"] = streamCursor;
            result.Add(new ExecutionStreamEntry(streamCursor, message with { AdditionalProperties = properties }));
        }

        return result;
    }

    /// <summary>
    /// 在唯一索引冲突后确认同一逻辑位置是否已经写入。
    /// 分段重放可能产生不同文本，但已经发布的位置不能覆盖或再次发送。
    /// </summary>
    private async Task<bool> ExistsAtPositionAsync(
        Guid executionId,
        int segmentIndex,
        int sequence,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
            return await dbContext
                .Set<DurableExecutionEventRecord>()
                .AsNoTracking()
                .AnyAsync(
                    item =>
                        item.ExecutionId == executionId
                        && item.SegmentIndex == segmentIndex
                        && item.Sequence == sequence,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsDatabaseFailure(exception))
        {
            throw CreateUnavailableException("verify", exception);
        }
    }

    /// <summary>
    /// 将数据库中的 segment 和 sequence 转换为不透明客户端 cursor。
    /// </summary>
    internal static string CreateCursor(int segmentIndex, int sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"{segmentIndex + 1}-{sequence}");

    /// <summary>
    /// 解析 PostgreSQL 实现生成的 cursor；Redis 的超大 terminal sequence 也能安全跳到下一分段。
    /// </summary>
    private static (int SegmentIndex, int? Sequence) ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || string.Equals(cursor, "0-0", StringComparison.Ordinal))
        {
            return (-1, 0);
        }

        var parts = cursor.Trim().Split('-', 2, StringSplitOptions.None);
        if (
            parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var encodedSegment)
            || encodedSegment <= 0
            || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var encodedSequence)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Execution stream cursor '{cursor}' is invalid.");
        }

        return (encodedSegment - 1, encodedSequence <= int.MaxValue ? (int)encodedSequence : null);
    }

    /// <summary>
    /// 判断异常是否来自数据库连接、命令或 EF Core 写入边界。
    /// </summary>
    private static bool IsDatabaseFailure(Exception exception) =>
        exception is DbException or DbUpdateException or TimeoutException;

    /// <summary>
    /// 将数据库消息流故障映射为统一的可降级错误。
    /// </summary>
    private static AgwException CreateUnavailableException(string operation, Exception exception) =>
        new(
            ErrorCodes.DurableExecutionUnavailable,
            $"PostgreSQL execution event stream {operation} is unavailable.",
            exception
        );
}
