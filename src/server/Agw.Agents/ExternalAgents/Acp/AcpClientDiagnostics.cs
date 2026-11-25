using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace Agw.Agents.ExternalAgents.Acp;

public static class AcpClientDiagnostics
{
    private const string Name = "Agw.Agents.AcpClient";

    public static ActivitySource ActivitySource { get; } = new(Name);
    public static Meter Meter { get; } = new(Name);
    public static Counter<long> Turns { get; } = Meter.CreateCounter<long>("agw.acp.client.turns");
    public static Histogram<double> TurnDuration { get; } =
        Meter.CreateHistogram<double>("agw.acp.client.turn.duration", "s");
    public static Counter<long> ProcessStarts { get; } = Meter.CreateCounter<long>("agw.acp.client.process_starts");
    public static Counter<long> PermissionRequests { get; } =
        Meter.CreateCounter<long>("agw.acp.client.permission_requests");
    public static Counter<long> RegistryFetches { get; } = Meter.CreateCounter<long>("agw.acp.client.registry_fetches");

    public enum TurnOutcome
    {
        Failed,
        Completed,
        Stopped,
        Interrupted,
        AuthRequired,
    }

    public static string OutcomeName(TurnOutcome outcome) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.ToString());
}
