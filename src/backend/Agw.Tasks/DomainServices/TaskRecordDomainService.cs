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

    public Dictionary<string, List<TaskRecord>> GroupByContext(IEnumerable<TaskRecord> records) =>
        Order(records)
            .GroupBy(record => record.ContextId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    public ProjectTask? FindTask(
        string sessionId,
        IReadOnlyList<ProjectTask> tasks,
        IReadOnlyList<TaskRecord> records)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || tasks.Count == 0)
        {
            return null;
        }

        var directTask = tasks.FirstOrDefault(task =>
            task.ContextId == sessionId
            || string.Equals(task.Id.Normalize(), sessionId, StringComparison.OrdinalIgnoreCase));
        if (directTask != null)
        {
            return directTask;
        }

        var taskByContext = tasks.ToDictionary(task => task.ContextId, StringComparer.Ordinal);
        var latestMatch = records
            .Where(record => taskByContext.ContainsKey(record.ContextId))
            .OrderByDescending(record => record.UpdateTime ?? record.CreateTime)
            .ThenByDescending(record => record.CreateTime)
            .FirstOrDefault();

        return latestMatch == null
            ? null
            : taskByContext.GetValueOrDefault(latestMatch.ContextId);
    }

    public bool ShouldDeleteTask(ProjectTask task) =>
        !Guid.TryParse(task.ProjectId, out _);
}
