using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Domain.Behaviors;

public sealed class ProjectConversationChatHistoryBehavior
{
    private readonly ProjectConversationChatHistory _record;

    public ProjectConversationChatHistoryBehavior(ProjectConversationChatHistory record)
    {
        _record = record;
    }

    /// <summary>
    /// <para>让记录进入所属任务的结束状态；任务成功时清除任务错误。</para>
    /// <para>Moves the record to its task's final status; a successful task clears the task error.</para>
    /// </summary>
    public void FinishTask(TaskExecutionStatus outcome, string? errorMessage, DateTimeOffset finishedAt)
    {
        _record.Status = outcome;
        _record.TaskErrorMessage = outcome == TaskExecutionStatus.Succeeded ? null : errorMessage;
        _record.FinishedTime = finishedAt;
        _record.UpdateTime = finishedAt;
    }
}
