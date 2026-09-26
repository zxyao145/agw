using Agw.Projects.Domain.Behaviors;
using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Domain.Services;

/// <summary>
/// <para>跨越单条历史记录、作用于同一会话下多条记录集合的领域规则。</para>
/// <para>Domain rules that span the history records of a conversation instead of binding to a single history root.</para>
/// </summary>
public sealed class ConversationHistoryDomainService
{
    public IReadOnlyList<ProjectConversationChatHistory> Order(IEnumerable<ProjectConversationChatHistory> records) =>
        records
            .OrderBy(record => record.ConversationSequence ?? long.MinValue)
            .ThenBy(record => record.CreateTime)
            .ThenBy(record => record.UpdateTime ?? record.CreateTime)
            .ToList();

    public ProjectConversationChatHistory? GetLatest(IEnumerable<ProjectConversationChatHistory> records) =>
        Order(records).LastOrDefault();

    /// <summary>
    /// <para>结束一个仍在运行的任务：currentStatus 是调用方从同一 TaskId 的全部记录投影出的任务状态，这些记录一起进入结束状态。</para>
    /// <para>Finishes a running task: currentStatus is the status the caller projected from every record of the task ID, and those records move to the final status together.</para>
    /// </summary>
    public bool TryFinishTask(
        IReadOnlyList<ProjectConversationChatHistory> taskRecords,
        TaskExecutionStatus currentStatus,
        TaskExecutionStatus outcome,
        string? errorMessage,
        DateTimeOffset finishedAt
    )
    {
        if (currentStatus != TaskExecutionStatus.Running)
        {
            return false;
        }

        foreach (var record in taskRecords)
        {
            new ProjectConversationChatHistoryBehavior(record).FinishTask(outcome, errorMessage, finishedAt);
        }

        return true;
    }
}
