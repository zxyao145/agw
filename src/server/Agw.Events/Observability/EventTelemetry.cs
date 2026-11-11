using System.Diagnostics.Metrics;

namespace Agw.Events.Observability;

public static class EventTelemetry
{
    public static readonly Meter Meter = new("Agw.Events");
    public static readonly Counter<long> Received = Meter.CreateCounter<long>("agw.events.received");
    public static readonly Counter<long> PublishFailures = Meter.CreateCounter<long>("agw.events.publish_failures");
    public static readonly Histogram<long> Recovered = Meter.CreateHistogram<long>("agw.events.recovered");
    public static readonly Histogram<long> Backlog = Meter.CreateHistogram<long>("agw.events.backlog");
    public static readonly Histogram<double> PollDuration = Meter.CreateHistogram<double>(
        "agw.events.poll.duration",
        "s"
    );
    public static readonly Counter<long> PollFailures = Meter.CreateCounter<long>("agw.events.poll.failures");
    public static readonly Counter<long> DeliveryTransitions = Meter.CreateCounter<long>(
        "agw.events.delivery.transitions"
    );
    public static readonly Histogram<double> DeliveryDuration = Meter.CreateHistogram<double>(
        "agw.events.delivery.duration",
        "s"
    );
    public static readonly Counter<long> DeliveryRetries = Meter.CreateCounter<long>("agw.events.delivery.retries");
}
