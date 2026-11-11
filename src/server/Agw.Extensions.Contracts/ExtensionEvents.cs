using System.Text.Json;
using Agw.Shared.Contracts.Events;

namespace Agw.Extensions.Contracts;

public sealed record ExtensionEventHandlerDescriptor
{
    public required string Id { get; init; }
}

public sealed record ExtensionEventSourceProviderDescriptor
{
    public required string Id { get; init; }

    public JsonElement? ConfigSchema { get; init; }
}

public sealed record ExtensionEventDelivery
{
    public required CloudEventEnvelope Event { get; init; }

    public required Guid DeliveryId { get; init; }

    public required int Attempt { get; init; }

    public required Guid SubscriptionId { get; init; }

    public JsonElement? HandlerConfig { get; init; }

    public required Guid EventId { get; init; }

    public required Guid SourceId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public required Guid ChainId { get; init; }

    public Guid? ParentEventId { get; init; }

    public required int Hops { get; init; }

    public string? Summary { get; init; }

    public string? Link { get; init; }
}

public enum ExtensionEventHandlerResultKind
{
    Succeeded,
    Retry,
    Failed,
}

public sealed record ExtensionEventHandlerResult
{
    public static ExtensionEventHandlerResult Succeeded { get; } =
        new() { Kind = ExtensionEventHandlerResultKind.Succeeded };

    public required ExtensionEventHandlerResultKind Kind { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public string? Reason { get; init; }

    public static ExtensionEventHandlerResult Retry(TimeSpan after, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(after, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ExtensionEventHandlerResult
        {
            Kind = ExtensionEventHandlerResultKind.Retry,
            RetryAfter = after,
            Reason = reason,
        };
    }

    public static ExtensionEventHandlerResult Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ExtensionEventHandlerResult { Kind = ExtensionEventHandlerResultKind.Failed, Reason = reason };
    }
}

public interface IExtensionEventSourceProvider
{
    ValueTask ValidateConfigAsync(JsonElement config, CancellationToken cancellationToken);

    ValueTask<ExtensionSourcePollResult> PollAsync(
        ExtensionSourcePollRequest request,
        CancellationToken cancellationToken
    );
}

public sealed record ExtensionSourcePollRequest
{
    public required Guid SourceId { get; init; }

    public required JsonElement Config { get; init; }

    public JsonElement? Cursor { get; init; }

    public required bool Baseline { get; init; }
}

public sealed record ExtensionSourcePollResult
{
    public IReadOnlyList<CloudEventDraft> Events { get; init; } = [];

    public JsonElement? Cursor { get; init; }
}

public sealed record ExtensionEventHandlerInvocation
{
    public required string HandlerId { get; init; }

    public required string UserId { get; init; }

    public required ExtensionEventDelivery Delivery { get; init; }
}

public sealed record ExtensionSourcePollInvocation
{
    public required string ProviderId { get; init; }

    public required string UserId { get; init; }

    public required Guid InvocationId { get; init; }

    public required ExtensionSourcePollRequest Request { get; init; }
}
