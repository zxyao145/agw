using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Todo;

public class TaskGetToolParams
{
    [Description(
        """
        The ID of the task to retrieve.
        """
    )]
    public string TaskId { get; set; } = "";
}

public class TaskGetToolDetail
{
    public string Id { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ActiveForm { get; set; }
    public string? Owner { get; set; }
    public List<string> Blocks { get; set; } = [];
    public List<string> BlockedBy { get; set; } = [];
    public Dictionary<string, object?>? Metadata { get; set; }
}

public class TaskGetToolResult
{
    public TaskGetToolDetail? Task { get; set; }
    public bool Found { get; set; }
}

internal class TaskGetTool : IAgwTool
{
    public string Name => "task_get";

    public string Category => "Todo";

    [Description(
        """
        Use this tool to retrieve a task by its ID from the task list.

        ## When to Use This Tool

        - When you need the full description and context before starting work on a task
        - To understand task dependencies (what it blocks, what blocks it)
        - After being assigned a task, to get complete requirements

        ## Output

        Returns full task details:
        - Subject: Task title
        - Description: Detailed requirements and context
        - Status: 'pending', 'in_progress', or 'completed'
        - Blocks: Tasks waiting on this one to complete
        - BlockedBy: Tasks that must complete before this one can start

        ## Tips

        - After fetching a task, verify its BlockedBy list is empty before beginning work.
        - Use task_list to see all tasks in summary form.
        """
    )]
    public TaskGetToolResult Execute(TaskGetToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.TaskId))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "TaskId is required.");
        }

        var task = TodoTaskStore.Get(toolParams.TaskId);
        if (task is null)
        {
            return new TaskGetToolResult { Task = null, Found = false };
        }

        return new TaskGetToolResult
        {
            Found = true,
            Task = new TaskGetToolDetail
            {
                Id = task.Id,
                Subject = task.Subject,
                Description = task.Description,
                Status = task.Status,
                ActiveForm = task.ActiveForm,
                Owner = task.Owner,
                Blocks = [.. task.Blocks],
                BlockedBy = [.. task.BlockedBy],
                Metadata = task.Metadata
            }
        };
    }

    public AITool ToAITool()
    {
        Func<TaskGetToolParams, TaskGetToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}