using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agw.Events.Contracts.Transport;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;

namespace Agw.Infrastructure.Events.Transport;

public sealed class InProcessEventTransport : IEventTransport
{
    private readonly CloudEventEnvelopeSerializer _serializer;
    private readonly ConcurrentDictionary<string, Channel<QueuedEvent>> _channels = new(StringComparer.Ordinal);

    public InProcessEventTransport(CloudEventEnvelopeSerializer serializer)
    {
        _serializer = serializer;
    }

    public async ValueTask<string> PublishAsync(
        string topic,
        CloudEventEnvelope message,
        CancellationToken cancellationToken
    )
    {
        ValidateTopic(topic);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] payload = _serializer.Serialize(message);
        string messageId = Guid.CreateVersion7().ToString();
        await GetChannel(topic)
            .Writer.WriteAsync(new QueuedEvent(messageId, payload), cancellationToken)
            .ConfigureAwait(false);
        return messageId;
    }

    public async IAsyncEnumerable<TransportDelivery> ConsumeAsync(
        string topic,
        string consumer,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        ValidateTopicAndConsumer(topic, consumer);

        await foreach (
            QueuedEvent queuedEvent in GetChannel(topic).Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)
        )
        {
            yield return new TransportDelivery
            {
                Topic = topic,
                Message = _serializer.Deserialize(queuedEvent.Payload),
                MessageId = queuedEvent.MessageId,
                DeliveryCount = 1,
            };
        }
    }

    public ValueTask AckAsync(TransportDelivery delivery, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
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
            throw new AgwException(ErrorCodes.InvalidParam);
        cancellationToken.ThrowIfCancellationRequested();
        return EmptyAsync(cancellationToken);
    }

    private Channel<QueuedEvent> GetChannel(string topic) =>
        _channels.GetOrAdd(
            topic,
            static _ =>
                Channel.CreateUnbounded<QueuedEvent>(
                    new UnboundedChannelOptions
                    {
                        AllowSynchronousContinuations = false,
                        SingleReader = false,
                        SingleWriter = false,
                    }
                )
        );

    private static void ValidateTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            throw new AgwException(ErrorCodes.InvalidParam);
    }

    private static void ValidateTopicAndConsumer(string topic, string consumer)
    {
        ValidateTopic(topic);
        if (string.IsNullOrWhiteSpace(consumer))
            throw new AgwException(ErrorCodes.InvalidParam);
    }

    private static async IAsyncEnumerable<TransportDelivery> EmptyAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    private sealed record QueuedEvent(string MessageId, byte[] Payload);
}
