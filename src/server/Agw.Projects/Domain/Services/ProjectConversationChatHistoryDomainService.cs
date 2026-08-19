using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;

namespace Agw.Projects.Domain.Services;

public class ProjectConversationChatHistoryDomainService
{
    public IReadOnlyList<ProjectConversationChatHistory> Order(IEnumerable<ProjectConversationChatHistory> records) =>
        records
            .OrderBy(r => r.ConversationSequence ?? long.MinValue)
            .ThenBy(r => r.CreateTime)
            .ThenBy(r => r.UpdateTime ?? r.CreateTime)
            .ToList();

    public ProjectConversationChatHistory? GetLatest(IEnumerable<ProjectConversationChatHistory> records) =>
        Order(records).LastOrDefault();

    public Dictionary<string, List<ProjectConversationChatHistory>> GroupByTaskId(
        IEnumerable<ProjectConversationChatHistory> records
    ) =>
        Order(records)
            .GroupBy(record => record.TaskId.Normalize(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    public TaskProjection? FindTask(
        string taskId,
        IReadOnlyList<TaskProjection> tasks,
        IReadOnlyList<ProjectConversationChatHistory> records
    )
    {
        if (string.IsNullOrWhiteSpace(taskId) || tasks.Count == 0)
        {
            return null;
        }

        var directTask = tasks.FirstOrDefault(task =>
            string.Equals(task.TaskId.Normalize(), taskId, StringComparison.OrdinalIgnoreCase)
        );
        if (directTask != null)
        {
            return directTask;
        }

        var taskById = tasks.ToDictionary(task => task.TaskId.Normalize(), StringComparer.OrdinalIgnoreCase);
        var latestMatch = records
            .Where(record => taskById.ContainsKey(record.TaskId.Normalize()))
            .OrderByDescending(record => record.UpdateTime ?? record.CreateTime)
            .ThenByDescending(record => record.CreateTime)
            .FirstOrDefault();

        return latestMatch == null ? null : taskById.GetValueOrDefault(latestMatch.TaskId.Normalize());
    }

    public bool ShouldDeleteTask(TaskProjection task) => false;
}
