using System.Globalization;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agw.Agents.Execution.Messaging.Durable;

/// <summary>
/// PostgreSQL 已提交事件在 Redis Stream 中的投影：stream ID 由 turnSequence 确定，重复发布按 ID 去重。写入保护与序号分配只在 PostgreSQL 完成。
/// The Redis Stream projection of events committed in PostgreSQL: the stream ID follows the turnSequence and repeated publication is deduplicated by ID. Write protection and sequence allocation happen only in PostgreSQL.
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
    /// 按序号追加已提交的事件；序号不大于投影末尾的事件被跳过。
    /// Appends committed events in sequence order; events not beyond the projection's end are skipped.
    /// </summary>
    public async Task PublishAsync(
        Guid turnId,
        IReadOnlyList<TurnBroadcastEntry> entries,
        CancellationToken cancellationToken
    )
    {
        if (entries.Count == 0)
            return;
        var database = _connection.GetDatabase();
        var key = GetKey(turnId);
        var last = await GetLastSequenceAsync(turnId, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries.OrderBy(item => item.Sequence))
        {
            if (entry.Sequence <= last)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            await database
                .StreamAddAsync(
                    key,
                    [new NameValueEntry(PayloadField, JsonUtil.Serialize(entry.Message))],
                    CreateStreamId(entry.Sequence)
                )
                .ConfigureAwait(false);
            last = entry.Sequence;
        }
        await database
            .KeyExpireAsync(key, TimeSpan.FromMinutes(_eventStreamOptions.Redis.StreamTtlMinutes))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 读取序号大于 afterSequence 的投影事件。
    /// Reads projected events after afterSequence.
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
            var payload = entry.Values.FirstOrDefault(field => field.Name == PayloadField).Value;
            var message =
                JsonUtil.Deserialize<AgwMessage>(payload!)
                ?? throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    $"Execution stream entry '{entry.Id}' contains an invalid message."
                );
            result.Add(new TurnBroadcastEntry(ParseSequence(entry.Id!), message));
        }
        return result;
    }

    private static RedisKey GetKey(Guid turnId) => $"agw:turn:{turnId:N}:events";

    private static string CreateStreamId(long sequence) => string.Create(CultureInfo.InvariantCulture, $"{sequence}-0");

    private static long ParseSequence(string streamId) =>
        long.Parse(streamId.AsSpan(0, streamId.IndexOf('-', StringComparison.Ordinal)), CultureInfo.InvariantCulture);
}
