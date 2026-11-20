using Agw.Shared.Data.Entities.Events;
using Microsoft.AspNetCore.Http;

namespace Agw.Events.Application.Persistence;

public interface IEventSourcePollService
{
    Task<EventSourcePollTestResult> TestAsync(EventSource source, CancellationToken cancellationToken);
}

public interface IEventSourceScanService
{
    Task ScanDueAsync(CancellationToken cancellationToken);
}

public interface IEventWebhookReceiver
{
    Task<EventWebhookReceiveResult> ReceiveAsync(
        Guid sourceId,
        HttpContext context,
        CancellationToken cancellationToken
    );
}

public interface IEventWebhookSourceLookup
{
    Task<EventSource?> FindEnabledAsync(Guid sourceId, CancellationToken cancellationToken);
}

public sealed record EventSourcePollTestResult(int ItemCount);

public sealed record EventWebhookReceiveResult(Guid? EventId, bool Duplicate, bool Discarded);
