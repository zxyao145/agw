using Agw.Shared.Contracts.Tools.Abstractions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Todo;

public class TaskListToolParams
{
    // No input parameters – lists all non-internal tasks.
}

public class TaskListToolSummary
{
    public string Id { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Owner { get; set; }

    /// <summary>
    /// Open task IDs that must be resolved before this task can be claimed.
    /// Completed dependencies are filtered out so the list shows only what is
    /// actually blocking work right now (matches TS TaskListTool behaviour).
    /// </summary>
    public List<string> BlockedBy { get; set; } = [];
}

public class TaskListToolResult
{
    public List<TaskListToolSummary> Tasks { get; set; } = [];
    public int Count { get; set; }
}

internal class TaskListTool : IAgwTool
{
    public string Name => "task_list";

    public string Category => "Todo";

    [Description(
        """
        Use this tool to list all tasks in the task list.

        ## When to Use This Tool

        - To see what tasks are available to work on (status: 'pending', no owner, not blocked)
        - To check overall progress on the project
        - To find tasks that are blocked and need dependencies resolved
        - After completing a task, to check for newly unblocked work or claim the next available task
        - Prefer working on tasks in ID order (lowest ID first) when multiple tasks are available, as earlier tasks often set up context for later ones

        ## Output

        Returns a summary of each task:
        - Id: Task identifier (use with task_get, task_update)
        - Subject: Brief description of the task
        - Status: 'pending', 'in_progress', or 'completed'
        - Owner: Agent ID if assigned, empty if available
        - BlockedBy: List of open task IDs that must be resolved first

        Use task_get with a specific task ID to view full details including description.
        """
    )]
    public TaskListToolResult Execute(TaskListToolParams toolParams)
    {
        var allTasks = TodoTaskStore.List();

        // Build a set of resolved (completed) IDs to filter BlockedBy noise,
        // mirroring the TS TaskListTool behaviour.
        var resolvedIds = new HashSet<string>(
            allTasks.Where(t => t.Status == "completed").Select(t => t.Id));

        var summaries = allTasks
            .Select(t => new TaskListToolSummary
            {
                Id = t.Id,
                Subject = t.Subject,
                Status = t.Status,
                Owner = t.Owner,
                BlockedBy = t.BlockedBy.Where(id => !resolvedIds.Contains(id)).ToList()
            })
            .ToList();

        return new TaskListToolResult
        {
            Tasks = summaries,
            Count = summaries.Count
        };
    }

    public AITool ToAITool()
    {
        Func<TaskListToolParams, TaskListToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
