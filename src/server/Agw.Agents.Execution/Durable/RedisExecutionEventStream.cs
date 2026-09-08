using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 使用 Redis Stream 保存有 TTL 的流式消息。stream ID 由 segment 和 sequence 确定，便于分段重放去重。
/// </summary>
internal sealed class RedisExecutionEventStream : IExecutionEventStream
{
    private const string PayloadField = "payload";
    private const string TerminalSequence = "18446744073709551615";

    private readonly IConnectionMultiplexer _connection;
    private readonly ExecutionEventStreamOptions _eventStreamOptions;
    private readonly RedisExecutionStreamOptions _options;

    /// <summary>
    /// 创建使用共享 Redis connection 和执行配置的事件流。
    /// </summary>
    public RedisExecutionEventStream(IConnectionMultiplexer connection, IOptions<ExecutionRuntimeOptions> options)
    {
        _connection = connection;
        _eventStreamOptions = options.Value.Distributed.EventStream;
        _options = _eventStreamOptions.Redis;
    }

    /// <summary>
    /// 使用确定性 stream ID 追加消息，并刷新 execution stream 的 TTL。
    /// </summary>
    public ValueTask AppendAsync(
        Guid executionId,
        int segmentIndex,
        int sequence,
        AgwMessage message,
        CancellationToken cancellationToken
    ) => AppendBatchAsync(executionId, segmentIndex, [new(sequence, message)], cancellationToken);

    public async ValueTask AppendBatchAsync(
        Guid executionId,
        int segmentIndex,
        IReadOnlyList<ExecutionStreamWrite> messages,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (segmentIndex < 0 || messages.Any(entry => entry.Sequence < 0))
            throw new AgwException(ErrorCodes.InvalidParam, "Event stream positions must be non-negative.");
        if (messages.Count == 0)
            return;
        try
        {
            var database = _connection.GetDatabase();
            var key = GetKey(executionId);
            var entries = messages.DistinctBy(entry => entry.Sequence).OrderBy(entry => entry.Sequence).ToArray();
            var batch = database.CreateBatch();
            var writes = entries
                .Select(entry =>
                {
                    var id = CreateStreamId(segmentIndex, entry.Sequence, entry.IsTerminal);
                    return (
                        Id: id,
                        Task: batch.StreamAddAsync(key, [new NameValueEntry(PayloadField, entry.PayloadJson)], id)
                    );
                })
                .ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            batch.Execute();
            // Observe every task, including failures, before verifying duplicate positions.
            try
            {
                await Task.WhenAll(writes.Select(write => write.Task)).ConfigureAwait(false);
            }
            catch (RedisException) { }
            foreach (var write in writes)
            {
                try
                {
                    await write.Task.ConfigureAwait(false);
                }
                catch (RedisServerException exception)
                {
                    // Verify positions instead of depending on Redis's error wording. A later ID
                    // without this position is a permanent gap; unrelated failures remain unavailable.
                    var existing = await database.StreamRangeAsync(key, write.Id, "+", count: 1).ConfigureAwait(false);
                    if (existing.Length == 0)
                        throw;
                    if (existing[0].Id != write.Id)
                        throw new AgwException(
                            ErrorCodes.DurableExecutionConflict,
                            $"Execution stream entry '{write.Id}' is missing before an existing later position.",
                            exception
                        );
                }
            }
            await database.KeyExpireAsync(key, TimeSpan.FromMinutes(_options.StreamTtlMinutes)).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Redis Stream append is unavailable.",
                exception
            );
        }
    }

    /// <summary>
    /// 读取 cursor 之后的消息，并补充 executionId 与最新 streamCursor。
    /// </summary>
    public async Task<IReadOnlyList<ExecutionStreamEntry>> ReadAsync(
        Guid executionId,
        string? afterCursor,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var database = _connection.GetDatabase();
            var key = GetKey(executionId);
            var cursor = string.IsNullOrWhiteSpace(afterCursor) ? "0-0" : afterCursor.Trim();
            var entries = await database
                .StreamReadAsync(key, cursor, _eventStreamOptions.ReadBatchSize)
                .ConfigureAwait(false);
            var result = new List<ExecutionStreamEntry>(entries.Length);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cursor = entry.Id!;
                var payload = GetField(entry, PayloadField);
                if (payload.IsNullOrEmpty)
                {
                    continue;
                }

                var message =
                    JsonUtil.Deserialize<AgwMessage>(payload!)
                    ?? throw new AgwException(
                        ErrorCodes.DurableExecutionConflict,
                        $"Execution stream entry '{entry.Id}' contains an invalid message."
                    );
                var properties =
                    message.AdditionalProperties == null
                        ? new AdditionalPropertiesDictionary()
                        : new AdditionalPropertiesDictionary(message.AdditionalProperties);
                properties["executionId"] = executionId.ToString("D");
                properties["streamCursor"] = cursor;
                result.Add(new ExecutionStreamEntry(cursor, message with { AdditionalProperties = properties }));
            }

            return result;
        }
        catch (RedisException exception)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Redis Stream read is unavailable.",
                exception
            );
        }
    }

    /// <summary>
    /// 生成 execution 专属的 Redis Stream key。
    /// </summary>
    private static RedisKey GetKey(Guid executionId) => $"agw:execution:{executionId:N}:messages";

    /// <summary>
    /// 将 segment 和 sequence 映射为单调递增且可重放去重的 Redis stream ID。
    /// </summary>
    internal static string CreateStreamId(int segmentIndex, int sequence, bool terminal) =>
        $"{segmentIndex + 1}-{(terminal ? TerminalSequence : sequence)}";

    /// <summary>
    /// 从 Redis Stream entry 中读取指定字段。
    /// </summary>
    private static RedisValue GetField(StreamEntry entry, string name)
    {
        foreach (var field in entry.Values)
        {
            if (field.Name == name)
            {
                return field.Value;
            }
        }

        return RedisValue.Null;
    }
}
