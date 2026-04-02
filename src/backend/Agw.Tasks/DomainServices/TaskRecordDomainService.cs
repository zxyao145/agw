using Agw.Shared;
using Agw.Shared.Tasks.Entities;

namespace Agw.Domain.Services;

public class TaskRecordDomainService
{
    public IReadOnlyList<TaskRecord> Order(IEnumerable<TaskRecord> records) =>
        records
            .OrderBy(r => r.ConversationSequence ?? long.MinValue)
            .ThenBy(r => r.CreateTime)
            .ThenBy(r => r.UpdateTime ?? r.CreateTime)
            .ToList();

    public TaskRecord? GetLatest(IEnumerable<TaskRecord> records) =>
        Order(records).LastOrDefault();

    public Dictionary<string, List<TaskRecord>> GroupByTaskId(IEnumerable<TaskRecord> records) =>
        Order(records)
            .GroupBy(record => record.TaskId.Normalize(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    public ProjectTask? FindTask(
        string taskId,
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<TaskRecord> records)
    {
        if (string.IsNullOrWhiteSpace(taskId) || tasks.Count == 0)
        {
            return null;
        }

        var directTask = tasks.FirstOrDefault(task => string.Equals(task.Id.Normalize(), taskId, StringComparison.OrdinalIgnoreCase));
        if (directTask != null)
        {
            return directTask;
        }

        var taskById = tasks.ToDictionary(task => task.Id.Normalize(), StringComparer.OrdinalIgnoreCase);
        var latestMatch = records
            .Where(record => taskById.ContainsKey(record.TaskId.Normalize()))
            .OrderByDescending(record => record.UpdateTime ?? record.CreateTime)
            .ThenByDescending(record => record.CreateTime)
            .FirstOrDefault();

        return latestMatch == null
            ? null
            : taskById.GetValueOrDefault(latestMatch.TaskId.Normalize());
    }

    public bool ShouldDeleteTask(ProjectTask task) => false;
}
