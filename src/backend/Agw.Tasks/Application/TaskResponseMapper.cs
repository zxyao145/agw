using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Extensions;
using Agw.Tasks.Domain.Services;

using Microsoft.Extensions.AI;

namespace Agw.Tasks.Application;

public static class TaskResponseMapper
{
    public static TaskProjection ToTask(ProjectContext context, IReadOnlyList<TaskRecord> records)
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
            ProjectId = context.ProjectId,
            ContextId = context.ContextId,
            JobId = latestRecord.JobId,
            Title = context.Title,
            Status = latestRecord.Status,
            ErrorMessage = latestRecord.TaskErrorMessage ?? latestRecord.Error,
            FinishedTime = latestRecord.FinishedTime,
            CreateTime = orderedRecords.Min(record => record.CreateTime),
            UpdateTime = orderedRecords.Max(record => record.UpdateTime ?? record.CreateTime)
        };
    }

    public static TaskSummaryResponse ToSummaryResponse(TaskProjection task) =>
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
            GetStartedTime(task));

    public static TaskResponse ToResponse(
        TaskProjection task,
        IReadOnlyList<TaskRecord> records,
        IReadOnlyList<AgwMessage>? messages)
    {
        return new TaskResponse(
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
            messages);
    }

    public static IEnumerable<AgwMessage> ToAiMessages(TaskRecord record)
    {
        var message = record.ToChatMessage()?.ToAiMessage();
        if (message != null)
        {
            yield return message;
        }
    }

    public static int CountMessages(IEnumerable<TaskRecord> records) =>
        records.Sum(CountMessages);

    private static int CountMessages(TaskRecord record) =>
        record.ToChatMessage() == null ? 0 : 1;

    private static DateTime? GetStartedTime(TaskProjection task) =>
        task.Status == TaskExecutionStatus.Pending ? null : task.CreateTime;

    private static string GetInputText(TaskRecord? record)
    {
        if (record?.ToChatMessage()?.Role != ChatRole.User)
        {
            return string.Empty;
        }

        return record.GetText();
    }
}
