using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Extensions;
using Agw.Tasks.Domain.Services;

using Microsoft.Extensions.AI;

namespace Agw.Tasks.Application;

internal static class ProjectTaskResponseMapper
{
    public static ProjectTaskSummaryResponse ToSummaryResponse(ProjectTask task) =>
        new(
            task.Id,
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

    public static ProjectTaskResponse ToResponse(
        ProjectTask task,
        IReadOnlyList<TaskRecord> records,
        IReadOnlyList<AgwMessage>? messages)
    {
        return new ProjectTaskResponse(
            task.Id,
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

    private static DateTime? GetStartedTime(ProjectTask task) =>
        task.Status == ProjectTaskStatus.Pending ? null : task.CreateTime;

    private static string GetInputText(TaskRecord? record)
    {
        if (record?.ToChatMessage()?.Role != ChatRole.User)
        {
            return string.Empty;
        }

        return record.GetText();
    }
}
