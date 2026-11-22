using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Agw.Agents.ExternalAgents.A2A;

public static class A2AClientDiagnostics
{
    public const string SourceName = "Agw.Agents.A2AClient";

    private static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> Turns = Meter.CreateCounter<long>("agw.a2a.client.turns");
    private static readonly Histogram<double> TurnDuration = Meter.CreateHistogram<double>(
        "agw.a2a.client.turn.duration",
        "s"
    );
    private static readonly Counter<long> CardFetches = Meter.CreateCounter<long>("agw.a2a.client.card_fetches");
    private static readonly Counter<long> Resubscribes = Meter.CreateCounter<long>("agw.a2a.client.resubscribes");

    public static Activity? StartCard(string host) =>
        Source.StartActivity("a2a.client.card", ActivityKind.Client)?.SetTag("server.address", host);

    public static Activity? StartTurn(string agentId, string? host, string? binding, bool streaming) =>
        Source
            .StartActivity("a2a.client.turn", ActivityKind.Client)
            ?.SetTag("agw.agent.id", agentId)
            .SetTag("server.address", host)
            .SetTag("a2a.protocol_binding", binding)
            .SetTag("a2a.streaming", streaming);

    public static void RecordCard(Activity? activity, CardOutcome outcome)
    {
        var name = OutcomeName(outcome);
        activity
            ?.SetTag("outcome", name)
            .SetStatus(outcome == CardOutcome.Ok ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        CardFetches.Add(1, new KeyValuePair<string, object?>("outcome", name));
    }

    public static void RecordTurn(Activity? activity, TurnOutcome outcome, string? binding, TimeSpan duration)
    {
        var name = OutcomeName(outcome);
        activity
            ?.SetTag("outcome", name)
            .SetStatus(
                outcome is TurnOutcome.Completed or TurnOutcome.InputRequired
                    ? ActivityStatusCode.Ok
                    : ActivityStatusCode.Error
            );
        var tags = new TagList { { "outcome", name }, { "protocol_binding", binding } };
        Turns.Add(1, tags);
        TurnDuration.Record(duration.TotalSeconds, tags);
    }

    public static void RecordResubscribe() => Resubscribes.Add(1);

    public static string OutcomeName<T>(T outcome)
        where T : Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.ToString());

    public enum CardOutcome
    {
        FetchFailed,
        Invalid,
        Ok,
    }

    public enum TurnOutcome
    {
        Failed,
        Completed,
        InputRequired,
        Interrupted,
        Timeout,
        Unauthorized,
    }
}
