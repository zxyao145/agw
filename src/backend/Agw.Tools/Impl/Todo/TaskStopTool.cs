using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Todo;

public class TaskStopToolParams
{
    [Description(
        """
        The ID of the background task to stop.
        """
    )]
    public string? TaskId { get; set; }

    [Description(
        """
        Deprecated: use task_id instead. Kept for backward compatibility with the legacy KillShell tool.
        """
    )]
    public string? ShellId { get; set; }
}

public class TaskStopToolResult
{
    public string Message { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string? Command { get; set; }
}

internal class TaskStopTool : IAgwTool
{
    public string Name => "task_stop";

    public string Category => "Todo";

    [Description(
        """
        Use this tool to stop a running background task by its ID.

        - Stops a running background task by its ID
        - Takes a task_id parameter identifying the task to stop
        - Returns a success or failure status
        - Use this tool when you need to terminate a long-running task
        """
    )]
    public TaskStopToolResult Execute(TaskStopToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        var id = !string.IsNullOrWhiteSpace(toolParams.TaskId)
            ? toolParams.TaskId
            : toolParams.ShellId;

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Missing required parameter: task_id");
        }

        var output = TodoTaskStore.GetBackgroundOutput(id);
        if (output is null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, $"No task found with ID: {id}");
        }

        if (!string.Equals(output.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Task {id} is not running (status: {output.Status})");
        }

        TodoTaskStore.UpdateBackgroundOutput(id, o =>
        {
            o.Status = "stopped";
            o.ExitCode = -1;
        });

        var command = output.Description;
        return new TaskStopToolResult
        {
            Message = $"Successfully stopped task: {id} ({command})",
            TaskId = id,
            TaskType = output.TaskType,
            Command = command
        };
    }

    public AITool ToAITool()
    {
        Func<TaskStopToolParams, TaskStopToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
