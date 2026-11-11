using Xunit;

namespace Agw.Extensions.TaskPilot.Tests;

public sealed class TaskPilotDeliveryPolicyTests
{
    [Theory]
    [InlineData("head-2", "base-1", "deliver", "Preflight")]
    [InlineData("head-1", "base-2", "deliver", "Sync")]
    [InlineData("head-1", "base-1", "deliver-review", "Preflight")]
    [InlineData("head-1", "base-1", "", "Preflight")]
    [InlineData("head-1", "base-1", "deliver", "Merge")]
    public void GetPreMergeStage_CurrentRemoteState_RequiresReviewedHeadBaseAndLabels(
        string head,
        string baseSha,
        string label,
        string expected
    )
    {
        var originalLabels = new HashSet<string>(StringComparer.Ordinal) { "deliver" };
        var currentLabels = new HashSet<string>(StringComparer.Ordinal);
        if (label.Length != 0)
            currentLabels.Add(label);
        Assert.Equal(
            expected,
            TaskPilotDeliveryPolicy.GetPreMergeStage(head, "head-1", baseSha, "base-1", originalLabels, currentLabels)
        );
    }

    [Fact]
    public void GetFinalLabelOrder_MergedPullRequest_RemovesReviewBeforeDelivery()
    {
        Assert.Equal(
            ["deliver-attended", "deliver-review", "deliver"],
            TaskPilotDeliveryPolicy.GetFinalLabelOrder(true)
        );
        Assert.Equal(
            ["deliver-attended", "deliver", "deliver-review"],
            TaskPilotDeliveryPolicy.GetFinalLabelOrder(false)
        );
    }
}
