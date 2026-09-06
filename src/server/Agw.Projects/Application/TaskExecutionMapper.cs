using Agw.Projects.Domain.Services;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Application;

public static class TaskExecutionMapper
{
    public static TaskProjection ToTask(
        ProjectConversation context,
        IReadOnlyList<ProjectConversationChatHistory> records
    )
    {
        var orderedRecords = records
            .OrderBy(record => record.CreateTime)
            .ThenBy(record => record.UpdateTime ?? record.CreateTime)
            .ThenBy(record => record.Id)
            .ToList();
        var firstRecord = orderedRecords.First();
        var latestRecord = orderedRecords
            .OrderByDescending(record => record.UpdateTime ?? record.CreateTime)
            .ThenByDescending(record => record.CreateTime)
            .ThenByDescending(record => record.Id)
            .First();

        return new TaskProjection
        {
            TaskId = firstRecord.TaskId,
            ProjectConversationId = context.Id,
            Generation = context.Generation,
            ProjectId = context.ProjectId,
            ContextId = context.ContextId,
            JobId = latestRecord.JobId,
            Title = context.Title,
            Status = latestRecord.Status,
            ErrorMessage = latestRecord.TaskErrorMessage ?? latestRecord.Error,
            FinishedTime = latestRecord.FinishedTime,
            CreateTime = orderedRecords.Min(record => record.CreateTime),
            UpdateTime = orderedRecords.Max(record => record.UpdateTime ?? record.CreateTime),
        };
    }

    public static TaskExecutionSummary ToSummary(TaskProjection task) =>
        new(
            task.TaskId,
            task.ProjectId.Normalize(),
            task.ContextId,
            task.JobId,
            task.Status,
            task.Title,
            task.ErrorMessage,
            task.CreateTime,
            task.UpdateTime,
            task.FinishedTime,
            GetStartedTime(task)
        );

    public static TaskExecutionSnapshot ToSnapshot(
        TaskProjection task,
        IReadOnlyList<ProjectConversationChatHistory> records,
        IReadOnlyList<AgwMessage>? messages
    )
    {
        return new TaskExecutionSnapshot(
            task.TaskId,
            task.ProjectId.Normalize(),
            task.ContextId,
            task.JobId,
            task.Status,
            task.Title,
            GetInputText(records.LastOrDefault(record => record.ToChatMessage()?.Role == ChatRole.User)),
            task.ErrorMessage ?? records.LastOrDefault()?.Error,
            task.CreateTime,
            task.UpdateTime,
            GetStartedTime(task),
            task.FinishedTime,
            CountMessages(records),
            messages
        );
    }

    public static IEnumerable<AgwMessage> ToAiMessages(ProjectConversationChatHistory record)
    {
        var message = record.ToChatMessage()?.ToAiMessage();
        if (message != null)
        {
            yield return message;
        }
    }

    public static int CountMessages(IEnumerable<ProjectConversationChatHistory> records) => records.Sum(CountMessages);

    private static int CountMessages(ProjectConversationChatHistory record) => record.ToChatMessage() == null ? 0 : 1;

    private static DateTimeOffset? GetStartedTime(TaskProjection task) =>
        task.Status == TaskExecutionStatus.Pending ? null : task.CreateTime;

    private static string GetInputText(ProjectConversationChatHistory? record)
    {
        if (record?.ToChatMessage()?.Role != ChatRole.User)
        {
            return string.Empty;
        }

        return record.GetText();
    }
}
