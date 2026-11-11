using System.Runtime.CompilerServices;
using Agw.Events.Contracts.Transport;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Agw.Infrastructure.Events.Transport;

public sealed class RedisEventTransport : IEventTransport
{
    private const string ConsumerGroup = "agw";
    private const string CloudEventField = "cloudevent";
    private const int ReadBatchSize = 100;
    private const int ClaimBatchSize = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IConnectionMultiplexer _connection;
    private readonly CloudEventEnvelopeSerializer _serializer;
    private readonly string _serverId;
    private readonly int _streamMaxLength;
    private readonly ILogger<RedisEventTransport> _logger;

    public RedisEventTransport(
        IConnectionMultiplexer connection,
        CloudEventEnvelopeSerializer serializer,
        string serverId,
        ILogger<RedisEventTransport> logger,
        int streamMaxLength = 100_000
    )
    {
        if (
            connection is null
            || serializer is null
            || string.IsNullOrWhiteSpace(serverId)
            || serverId.Contains(':')
            || logger is null
        )
            throw InvalidParam();
        if (streamMaxLength < 1)
            throw InvalidParam();

        _connection = connection;
        _serializer = serializer;
        _serverId = serverId;
        _streamMaxLength = streamMaxLength;
        _logger = logger;
    }

    public async ValueTask<string> PublishAsync(
        string topic,
        CloudEventEnvelope message,
        CancellationToken cancellationToken
    )
    {
        ValidateTopic(topic);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = _serializer.Serialize(message);
        cancellationToken.ThrowIfCancellationRequested();

        var messageId = await _connection
            .GetDatabase()
            .StreamAddAsync(
                GetStreamKey(topic),
                [new NameValueEntry(CloudEventField, payload)],
                maxLength: _streamMaxLength,
                useApproximateMaxLength: true
            )
            .ConfigureAwait(false);
        return messageId.ToString();
    }

    public IAsyncEnumerable<TransportDelivery> ConsumeAsync(
        string topic,
        string consumer,
        CancellationToken cancellationToken
    )
    {
        ValidateTopicAndConsumer(topic, consumer);
        return ConsumeCoreAsync(topic, consumer, cancellationToken);
    }

    public async ValueTask AckAsync(TransportDelivery delivery, CancellationToken cancellationToken)
    {
        if (
            delivery is null
            || string.IsNullOrWhiteSpace(delivery.Topic)
            || string.IsNullOrWhiteSpace(delivery.MessageId)
        )
            throw InvalidParam();
        cancellationToken.ThrowIfCancellationRequested();

        await _connection
            .GetDatabase()
            .StreamAcknowledgeAsync(GetStreamKey(delivery.Topic), ConsumerGroup, delivery.MessageId)
            .ConfigureAwait(false);
    }

    public IAsyncEnumerable<TransportDelivery> ClaimStaleAsync(
        string topic,
        string consumer,
        TimeSpan minIdle,
        CancellationToken cancellationToken
    )
    {
        ValidateTopicAndConsumer(topic, consumer);
        if (minIdle < TimeSpan.Zero)
            throw InvalidParam();
        return ClaimStaleCoreAsync(topic, consumer, minIdle, cancellationToken);
    }

    private async IAsyncEnumerable<TransportDelivery> ConsumeCoreAsync(
        string topic,
        string consumer,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var streamKey = GetStreamKey(topic);
        var database = _connection.GetDatabase();
        await EnsureConsumerGroupAsync(database, streamKey, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await database
                .StreamReadGroupAsync(streamKey, ConsumerGroup, consumer, ">", ReadBatchSize)
                .ConfigureAwait(false);
            if (entries.Length == 0)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return CreateDelivery(topic, entry, deliveryCount: 1);
            }
        }
    }

    private async IAsyncEnumerable<TransportDelivery> ClaimStaleCoreAsync(
        string topic,
        string consumer,
        TimeSpan minIdle,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var streamKey = GetStreamKey(topic);
        var database = _connection.GetDatabase();
        await EnsureConsumerGroupAsync(database, streamKey, cancellationToken).ConfigureAwait(false);

        var minIdleMilliseconds = (long)Math.Ceiling(minIdle.TotalMilliseconds);
        RedisValue startId = "0-0";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claim = await database
                .StreamAutoClaimAsync(streamKey, ConsumerGroup, consumer, minIdleMilliseconds, startId, ClaimBatchSize)
                .ConfigureAwait(false);

            foreach (var entry in claim.ClaimedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deliveryCount = await GetDeliveryCountAsync(database, streamKey, consumer, entry.Id)
                    .ConfigureAwait(false);
                if (deliveryCount is not int count)
                    continue;
                yield return CreateDelivery(topic, entry, count);
            }

            startId = claim.NextStartId;
            if (startId == "0-0")
                yield break;
        }
    }

    private async Task EnsureConsumerGroupAsync(
        IDatabase database,
        RedisKey streamKey,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await database
                .StreamCreateConsumerGroupAsync(streamKey, ConsumerGroup, "0-0", createStream: true)
                .ConfigureAwait(false);
        }
        catch (RedisServerException exception)
            when (exception.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                exception,
                "Redis consumer group already exists for stream {StreamKey}.",
                streamKey.ToString()
            );
            return;
        }
    }

    private async Task<int?> GetDeliveryCountAsync(
        IDatabase database,
        RedisKey streamKey,
        string consumer,
        RedisValue messageId
    )
    {
        var pending = await database
            .StreamPendingMessagesAsync(
                streamKey,
                ConsumerGroup,
                count: 1,
                consumerName: consumer,
                minId: messageId,
                maxId: messageId
            )
            .ConfigureAwait(false);
        if (pending.Length != 1 || pending[0].MessageId != messageId || pending[0].ConsumerName != consumer)
            return null;

        return checked((int)pending[0].DeliveryCount);
    }

    private TransportDelivery CreateDelivery(string topic, StreamEntry entry, int deliveryCount)
    {
        if (entry.Values.Length != 1 || entry.Values[0].Name != CloudEventField)
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Redis stream entry does not contain a valid CloudEvents payload."
            );

        var payload = (ReadOnlyMemory<byte>)entry.Values[0].Value;
        return new TransportDelivery
        {
            Topic = topic,
            Message = _serializer.Deserialize(payload),
            MessageId = entry.Id.ToString(),
            DeliveryCount = deliveryCount,
        };
    }

    private RedisKey GetStreamKey(string topic) => $"agw:{_serverId}:{topic}";

    private static void ValidateTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            throw InvalidParam();
    }

    private static void ValidateTopicAndConsumer(string topic, string consumer)
    {
        ValidateTopic(topic);
        if (string.IsNullOrWhiteSpace(consumer))
            throw InvalidParam();
    }

    private static AgwException InvalidParam() => new(ErrorCodes.InvalidParam);
}
