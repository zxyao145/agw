using System.Diagnostics.Metrics;

namespace Agw.Extensions.TaskPilot;

internal static class TaskPilotTelemetry
{
    private static readonly Meter Meter = new("Agw.Extensions.TaskPilot");
    private static readonly Counter<long> RoundTransitions = Meter.CreateCounter<long>("taskpilot.round.transitions");
    private static readonly Counter<long> ReceiptRepairs = Meter.CreateCounter<long>("taskpilot.receipt.repairs");

    public static void RecordRoundTransition(TaskPilotRoundState state) =>
        RoundTransitions.Add(1, new KeyValuePair<string, object?>("state", state.ToString()));

    public static void RecordReceiptRepair() => ReceiptRepairs.Add(1);
}
