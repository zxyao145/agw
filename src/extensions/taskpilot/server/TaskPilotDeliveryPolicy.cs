namespace Agw.Extensions.TaskPilot;

public static class TaskPilotDeliveryPolicy
{
    public static string GetPreMergeStage(
        string currentHead,
        string reviewedHead,
        string currentBase,
        string reviewedBase,
        IReadOnlySet<string> reviewedLabels,
        IReadOnlySet<string> currentLabels
    )
    {
        if (
            currentHead != reviewedHead
            || !reviewedLabels.SetEquals(currentLabels)
            || (!currentLabels.Contains("deliver") && !currentLabels.Contains("deliver-review"))
        )
            return TaskPilotDeliveryStages.Preflight;
        if (currentBase != reviewedBase)
            return TaskPilotDeliveryStages.Sync;
        return TaskPilotDeliveryStages.Merge;
    }

    public static IReadOnlyList<string> GetFinalLabelOrder(bool merged) =>
        merged ? ["deliver-attended", "deliver-review", "deliver"] : ["deliver-attended", "deliver", "deliver-review"];
}

internal static class TaskPilotDeliveryStages
{
    public const string Preflight = nameof(Preflight);
    public const string ReadConfig = nameof(ReadConfig);
    public const string Sync = nameof(Sync);
    public const string Snapshot = nameof(Snapshot);
    public const string Review = nameof(Review);
    public const string CheckWait = nameof(CheckWait);
    public const string PreMergeVerify = nameof(PreMergeVerify);
    public const string Merge = nameof(Merge);
    public const string ShipWait = nameof(ShipWait);
    public const string HealthVerify = nameof(HealthVerify);
    public const string Finalize = nameof(Finalize);
    public const string Done = nameof(Done);
    public const string ReviewOnlyDone = nameof(ReviewOnlyDone);
    public const string FindingsReported = nameof(FindingsReported);
    public const string Failed = nameof(Failed);
}
