using System.Globalization;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agw.Agents.Execution.Messaging.Durable;

/// <summary>
/// PostgreSQL 已提交事件在 Redis Stream 中的投影：stream ID 由 turnSequence 确定，重复发布按 ID 去重。写入保护与序号分配只在 PostgreSQL 完成。
/// 投影保存与 PostgreSQL 相同的未加序号载荷，读取时再加上 turnId 与 turnSequence。
/// The Redis Stream projection of events committed in PostgreSQL: the stream ID follows the turnSequence and repeated publication is deduplicated by ID. Write protection and sequence allocation happen only in PostgreSQL.
/// The projection stores the same unnumbered payload as PostgreSQL and adds the turnId and turnSequence when read.
/// </summary>
internal sealed class RedisExecutionEventProjection
{
    private const string PayloadField = "payload";

    private readonly IConnectionMultiplexer _connection;
    private readonly ExecutionEventStreamOptions _eventStreamOptions;

    public RedisExecutionEventProjection(IConnectionMultiplexer connection, IOptions<ExecutionRuntimeOptions> options)
    {
        _connection = connection;
        _eventStreamOptions = options.Value.Distributed.EventStream;
    }

    /// <summary>
    /// 投影中已经存在的最大序号；没有投影时为 0。
    /// The largest sequence already in the projection; 0 without a projection.
    /// </summary>
    public async Task<long> GetLastSequenceAsync(Guid turnId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var last = await _connection
            .GetDatabase()
            .StreamRangeAsync(GetKey(turnId), "-", "+", count: 1, messageOrder: Order.Descending)
            .ConfigureAwait(false);
        return last.Length == 0 ? 0 : ParseSequence(last[0].Id!);
    }

    /// <summary>
    /// 按序号追加 lastSequence 之后的已提交事件；全部命令放在一个批次里发送，同一连接上的命令保持顺序，显式的递增 stream ID 仍按序写入。
    /// Appends committed events after lastSequence in sequence order; every command goes out in one batch, and commands on one connection keep their order, so the explicit increasing stream IDs are still written in order.
    /// </summary>
    public async Task PublishAsync(
        Guid turnId,
        IReadOnlyList<TurnBroadcastEntry> entries,
        long lastSequence,
        CancellationToken cancellationToken
    )
    {
        if (entries.Count == 0)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        var key = GetKey(turnId);
        var batch = _connection.GetDatabase().CreateBatch();
        var commands = new List<Task>(entries.Count + 1);
        foreach (var entry in entries.OrderBy(item => item.Sequence))
        {
            if (entry.Sequence <= lastSequence)
                continue;
            commands.Add(
                batch.StreamAddAsync(
                    key,
                    [
                        new NameValueEntry(
                            PayloadField,
                            entry.PayloadJson
                                ?? throw new AgwException(
                                    ErrorCodes.DurableExecutionConflict,
                                    $"Event {entry.Sequence} of turn '{turnId}' has no committed payload."
                                )
                        ),
                    ],
                    CreateStreamId(entry.Sequence)
                )
            );
            lastSequence = entry.Sequence;
        }
        commands.Add(batch.KeyExpireAsync(key, TimeSpan.FromMinutes(_eventStreamOptions.Redis.StreamTtlMinutes)));
        batch.Execute();
        await Task.WhenAll(commands).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取序号大于 afterSequence 的投影事件；载荷按 UTF-8 字节解析。
    /// Reads projected events after afterSequence; payloads are parsed from their UTF-8 bytes.
    /// </summary>
    public async Task<IReadOnlyList<TurnBroadcastEntry>> ReadAsync(
        Guid turnId,
        long afterSequence,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _connection
            .GetDatabase()
            .StreamReadAsync(GetKey(turnId), CreateStreamId(afterSequence), _eventStreamOptions.ReadBatchSize)
            .ConfigureAwait(false);
        var result = new List<TurnBroadcastEntry>(entries.Length);
        foreach (var entry in entries)
        {
            var id = (string)entry.Id!;
            var payload = entry.Values.FirstOrDefault(field => field.Name == PayloadField).Value;
            var sequence = ParseSequence(id);
            result.Add(
                new TurnBroadcastEntry(
                    sequence,
                    DurableExecutionEvents.ReadPayload(((ReadOnlyMemory<byte>)payload).Span, turnId, sequence, id)
                )
            );
        }
        return result;
    }

    private static RedisKey GetKey(Guid turnId) => $"agw:turn:{turnId:N}:events";

    private static string CreateStreamId(long sequence) => string.Create(CultureInfo.InvariantCulture, $"{sequence}-0");

    private static long ParseSequence(string streamId) =>
        long.Parse(streamId.AsSpan(0, streamId.IndexOf('-', StringComparison.Ordinal)), CultureInfo.InvariantCulture);
}
