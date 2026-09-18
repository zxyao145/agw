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

    private static readonly Counter<long> OAuth2Logins = Meter.CreateCounter<long>("agw.auth.oauth2.login");
    private static readonly Counter<long> OAuth2TokenExchanges = Meter.CreateCounter<long>(
        "agw.auth.oauth2.token_exchange"
    );
    private static readonly Counter<long> OAuth2UserInfoRequests = Meter.CreateCounter<long>(
        "agw.auth.oauth2.userinfo"
    );
    private static readonly Counter<long> OAuth2AccessTokenValidations = Meter.CreateCounter<long>(
        "agw.auth.oauth2.access_token_validation"
    );
    private static readonly Counter<long> OAuth2PkceRequests = Meter.CreateCounter<long>("agw.auth.oauth2.pkce");
    private static readonly Histogram<double> OAuth2Callbacks = Meter.CreateHistogram<double>(
        "agw.auth.oauth2.callback.duration",
        "s"
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

    public static void OAuth2Login(string provider, string client, string result) =>
        OAuth2Logins.Add(1, new("provider", provider), new("client", client), new("result", result));

    public static void OAuth2TokenExchange(string provider, string result) =>
        OAuth2TokenExchanges.Add(1, new("provider", provider), new("result", result));

    public static void OAuth2UserInfo(string provider, string result) =>
        OAuth2UserInfoRequests.Add(1, new("provider", provider), new("result", result));

    public static void OAuth2AccessTokenValidation(string provider, string result) =>
        OAuth2AccessTokenValidations.Add(1, new("provider", provider), new("result", result));

    public static void OAuth2Pkce(string provider, bool enabled) =>
        OAuth2PkceRequests.Add(1, new("provider", provider), new("enabled", enabled ? "true" : "false"));

    public static void OAuth2Callback(string provider, string result, long started) =>
        OAuth2Callbacks.Record(
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            new("provider", provider),
            new("result", result)
        );
}
