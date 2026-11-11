using System.Diagnostics.Metrics;

namespace Agw.Notifications.Observability;

public static class NotificationTelemetry
{
    public static readonly Meter Meter = new("Agw.Notifications");
    public static readonly Counter<long> Sends = Meter.CreateCounter<long>("agw.notifications.sends");
    public static readonly Histogram<double> SendDuration = Meter.CreateHistogram<double>(
        "agw.notifications.send.duration",
        "s"
    );
}
