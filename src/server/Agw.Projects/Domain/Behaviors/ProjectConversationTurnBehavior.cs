using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Domain.Behaviors;

public sealed class ProjectConversationTurnBehavior
{
    private readonly ProjectConversationTurn _turn;

    public ProjectConversationTurnBehavior(ProjectConversationTurn turn)
    {
        _turn = turn;
    }

    /// <summary>
    /// <para>受理 Turn：Turn 从受理状态开始，起点是受理时对话的下一个序号。</para>
    /// <para>Accepts the turn: it starts in the accepted state at the conversation's next sequence at acceptance.</para>
    /// </summary>
    public void Accept(long firstSequence, DateTimeOffset startedAt)
    {
        _turn.Status = ProjectConversationTurnStatus.Accepted;
        _turn.FirstSequence = firstSequence;
        _turn.StartedAt = startedAt;
    }

    /// <summary>
    /// <para>同一用户在同一对话重发同一 turnId 才是同一次受理。</para>
    /// <para>Only the same user resending the same turnId in the same conversation is the same acceptance.</para>
    /// </summary>
    public bool IsSameAcceptance(Guid conversationId) => _turn.ProjectConversationId == conversationId;

    public bool TryMarkRunning()
    {
        if (_turn.Status != ProjectConversationTurnStatus.Accepted)
        {
            return false;
        }

        _turn.Status = ProjectConversationTurnStatus.Running;
        return true;
    }

    public bool TryCompleteStep(int stepCount)
    {
        if (IsFinished() || stepCount <= _turn.StepCount)
        {
            return false;
        }

        _turn.StepCount = stepCount;
        return true;
    }

    /// <summary>
    /// <para>以结束状态结束 Turn，lastSequence 是该 Turn 写入的最后一条对话记录序号；已经结束的 Turn 保持第一次结束时的结果。</para>
    /// <para>Finishes the turn with a terminal status, where lastSequence is the last record sequence the turn wrote; a finished turn keeps its first result.</para>
    /// </summary>
    public bool TryFinish(
        ProjectConversationTurnStatus status,
        int stepCount,
        string? errorCode,
        DateTimeOffset finishedAt,
        long? lastSequence
    )
    {
        if (!IsTerminal(status))
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Turn status '{status}' is not terminal.");
        }

        if (IsFinished())
        {
            return false;
        }

        _turn.Status = status;
        _turn.StepCount = Math.Max(_turn.StepCount, stepCount);
        _turn.ErrorCode = errorCode;
        _turn.FinishedAt = finishedAt;
        _turn.LastSequence = lastSequence;
        return true;
    }

    private bool IsFinished() => IsTerminal(_turn.Status);

    private static bool IsTerminal(ProjectConversationTurnStatus status) =>
        status
            is ProjectConversationTurnStatus.Completed
                or ProjectConversationTurnStatus.Failed
                or ProjectConversationTurnStatus.Interrupted;
}
