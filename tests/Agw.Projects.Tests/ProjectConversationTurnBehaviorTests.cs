using Agw.Projects.Domain.Behaviors;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Tests;

public class ProjectConversationTurnBehaviorTests
{
    private static readonly DateTimeOffset StartedAt = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FinishedAt = new(2024, 1, 1, 0, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Accept_NewTurn_StartsAcceptedAtSequence()
    {
        var turn = new ProjectConversationTurn { Status = ProjectConversationTurnStatus.Running };

        new ProjectConversationTurnBehavior(turn).Accept(7, StartedAt);

        Assert.Equal(ProjectConversationTurnStatus.Accepted, turn.Status);
        Assert.Equal(7, turn.FirstSequence);
        Assert.Equal(StartedAt, turn.StartedAt);
    }

    [Fact]
    public void IsSameAcceptance_OtherConversation_ReturnsFalse()
    {
        var turn = new ProjectConversationTurn { ProjectConversationId = Guid.CreateVersion7() };

        var sameAcceptance = new ProjectConversationTurnBehavior(turn).IsSameAcceptance(Guid.CreateVersion7());

        Assert.False(sameAcceptance);
    }

    [Fact]
    public void TryMarkRunning_AcceptedTurn_MovesToRunning()
    {
        var turn = new ProjectConversationTurn { Status = ProjectConversationTurnStatus.Accepted };

        var changed = new ProjectConversationTurnBehavior(turn).TryMarkRunning();

        Assert.True(changed);
        Assert.Equal(ProjectConversationTurnStatus.Running, turn.Status);
    }

    [Fact]
    public void TryMarkRunning_FinishedTurn_KeepsStatus()
    {
        var turn = new ProjectConversationTurn { Status = ProjectConversationTurnStatus.Completed };

        var changed = new ProjectConversationTurnBehavior(turn).TryMarkRunning();

        Assert.False(changed);
        Assert.Equal(ProjectConversationTurnStatus.Completed, turn.Status);
    }

    [Theory]
    [InlineData(ProjectConversationTurnStatus.Running, 3, 3, false)]
    [InlineData(ProjectConversationTurnStatus.Running, 3, 2, false)]
    [InlineData(ProjectConversationTurnStatus.Running, 3, 4, true)]
    [InlineData(ProjectConversationTurnStatus.Failed, 3, 4, false)]
    public void TryCompleteStep_StepCount_OnlyAdvancesUnfinishedTurn(
        ProjectConversationTurnStatus status,
        int currentStepCount,
        int completedStepCount,
        bool expectedChanged
    )
    {
        var turn = new ProjectConversationTurn { Status = status, StepCount = currentStepCount };

        var changed = new ProjectConversationTurnBehavior(turn).TryCompleteStep(completedStepCount);

        Assert.Equal(expectedChanged, changed);
        Assert.Equal(expectedChanged ? completedStepCount : currentStepCount, turn.StepCount);
    }

    [Fact]
    public void TryFinish_RunningTurn_RecordsResult()
    {
        var turn = new ProjectConversationTurn { Status = ProjectConversationTurnStatus.Running, StepCount = 5 };

        var finished = new ProjectConversationTurnBehavior(turn).TryFinish(
            ProjectConversationTurnStatus.Failed,
            3,
            "model_error",
            FinishedAt,
            12
        );

        Assert.True(finished);
        Assert.Equal(ProjectConversationTurnStatus.Failed, turn.Status);
        Assert.Equal(5, turn.StepCount);
        Assert.Equal("model_error", turn.ErrorCode);
        Assert.Equal(FinishedAt, turn.FinishedAt);
        Assert.Equal(12, turn.LastSequence);
    }

    [Fact]
    public void TryFinish_FinishedTurn_KeepsFirstResult()
    {
        var turn = new ProjectConversationTurn
        {
            Status = ProjectConversationTurnStatus.Interrupted,
            ErrorCode = "interrupted",
            FinishedAt = StartedAt,
        };

        var finished = new ProjectConversationTurnBehavior(turn).TryFinish(
            ProjectConversationTurnStatus.Completed,
            8,
            null,
            FinishedAt,
            20
        );

        Assert.False(finished);
        Assert.Equal(ProjectConversationTurnStatus.Interrupted, turn.Status);
        Assert.Equal("interrupted", turn.ErrorCode);
        Assert.Equal(StartedAt, turn.FinishedAt);
    }

    [Theory]
    [InlineData(ProjectConversationTurnStatus.Accepted)]
    [InlineData(ProjectConversationTurnStatus.Running)]
    public void TryFinish_NonTerminalStatus_ThrowsInvalidParam(ProjectConversationTurnStatus status)
    {
        var turn = new ProjectConversationTurn { Status = ProjectConversationTurnStatus.Running };

        var exception = Assert.Throws<AgwException>(() =>
            new ProjectConversationTurnBehavior(turn).TryFinish(status, 1, null, FinishedAt, null)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal(ProjectConversationTurnStatus.Running, turn.Status);
    }
}
