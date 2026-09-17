using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agw.Auth.Application;

public static class OidcTelemetry
{
    private static readonly Meter Meter = new("Agw.Auth");
    private static readonly Counter<long> Logins = Meter.CreateCounter<long>("agw.auth.oidc.login");
    private static readonly Histogram<double> Callbacks = Meter.CreateHistogram<double>(
        "agw.auth.oidc.callback.duration",
        "s"
    );
    private static readonly Counter<long> Exchanges = Meter.CreateCounter<long>("agw.auth.desktop.exchange");
    private static readonly Histogram<double> ExchangeDurations = Meter.CreateHistogram<double>(
        "agw.auth.desktop.exchange.duration",
        "s"
    );
    private static readonly Counter<long> CleanupFailures = Meter.CreateCounter<long>(
        "agw.auth.desktop.cleanup.failure"
    );

    public static void Login(string provider, string client, string result) =>
        Logins.Add(1, new("provider", provider), new("client", client), new("result", result));

    public static void Callback(string provider, string result, long started) =>
        Callbacks.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            new("provider", provider),
            new("result", result)
        );

    public static void Exchange(string result, long started)
    {
        Exchanges.Add(1, new KeyValuePair<string, object?>("result", result));
        ExchangeDurations.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            new KeyValuePair<string, object?>("result", result)
        );
    }

    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("agw.auth.oidc.failure");

    public static void Failure(string provider, string client, string stage, string category) =>
        Failures.Add(
            1,
            new("provider", provider),
            new("client", client),
            new("stage", stage),
            new("category", category)
        );

    public static void CleanupFailed() => CleanupFailures.Add(1);
}
