using Agw.Shared.Contracts.Events;

namespace Agw.Events.Contracts.Transport;

public interface IEventTransport
{
    ValueTask<string> PublishAsync(string topic, CloudEventEnvelope message, CancellationToken cancellationToken);

    IAsyncEnumerable<TransportDelivery> ConsumeAsync(
        string topic,
        string consumer,
        CancellationToken cancellationToken
    );

    ValueTask AckAsync(TransportDelivery delivery, CancellationToken cancellationToken);

    IAsyncEnumerable<TransportDelivery> ClaimStaleAsync(
        string topic,
        string consumer,
        TimeSpan minIdle,
        CancellationToken cancellationToken
    );
}

public sealed record TransportDelivery
{
    public required string Topic { get; init; }

    public required CloudEventEnvelope Message { get; init; }

    public required string MessageId { get; init; }

    public required int DeliveryCount { get; init; }
}
