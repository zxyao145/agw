using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Todo;

public class TaskOutputToolParams
{
    [Description(
        """
        The ID of the task to get output from.
        """
    )]
    public string TaskId { get; set; } = "";

    [Description(
        """
        Whether to wait for the task to complete (default: true).
        Set to false for a non-blocking check of current status.
        """
    )]
    public bool Block { get; set; } = true;

    [Description(
        """
        Maximum wait time in milliseconds when blocking (default: 30000, max: 600000).
        """
    )]
    public int Timeout { get; set; } = 30000;
}

public class TaskOutputToolTaskDetail
{
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string Status { get; set; } = "";
    public string Description { get; set; } = "";
    public string Output { get; set; } = "";
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
}

public class TaskOutputToolResult
{
    /// <summary>
    /// 'success', 'timeout', or 'not_ready'
    /// </summary>
    public string RetrievalStatus { get; set; } = "";
    public TaskOutputToolTaskDetail? Task { get; set; }
}

internal class TaskOutputTool : IAgwTool
{
    public string Name => "task_output";

    public string Category => "Todo";

    [Description(
        """
        Use this tool to retrieve output from a running or completed background task.

        - Retrieves output from a running or completed task (background shell, agent, or remote session)
        - Takes a task_id parameter identifying the task
        - Returns the task output along with status information
        - Use block=true (default) to wait for task completion
        - Use block=false for non-blocking check of current status
        - Task IDs can be found using the task_list tool

        ## Output

        Returns:
        - RetrievalStatus: 'success', 'timeout', or 'not_ready'
        - Task: details including output, status, exit code, and error
        """
    )]
    public TaskOutputToolResult Execute(TaskOutputToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.TaskId))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Task ID is required.");
        }

        // Clamp timeout.
        var timeoutMs = Math.Clamp(toolParams.Timeout, 0, 600_000);

        var backgroundOutput = TodoTaskStore.GetBackgroundOutput(toolParams.TaskId);
        if (backgroundOutput is null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, $"No task found with ID: {toolParams.TaskId}");
        }

        if (!toolParams.Block)
        {
            // Non-blocking: return current state immediately.
            var isTerminal = !string.Equals(backgroundOutput.Status, "running", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(backgroundOutput.Status, "pending", StringComparison.OrdinalIgnoreCase);
            return new TaskOutputToolResult
            {
                RetrievalStatus = isTerminal ? "success" : "not_ready",
                Task = MapDetail(backgroundOutput)
            };
        }

        // Blocking: poll until terminal or timeout.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            backgroundOutput = TodoTaskStore.GetBackgroundOutput(toolParams.TaskId);
            if (backgroundOutput is null)
            {
                return new TaskOutputToolResult
                {
                    RetrievalStatus = "timeout",
                    Task = null
                };
            }

            var isTerminal = !string.Equals(backgroundOutput.Status, "running", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(backgroundOutput.Status, "pending", StringComparison.OrdinalIgnoreCase);
            if (isTerminal)
            {
                return new TaskOutputToolResult
                {
                    RetrievalStatus = "success",
                    Task = MapDetail(backgroundOutput)
                };
            }

            Thread.Sleep(100);
        }

        // Timed out — return last known state.
        return new TaskOutputToolResult
        {
            RetrievalStatus = "timeout",
            Task = MapDetail(backgroundOutput)
        };
    }

    private static TaskOutputToolTaskDetail MapDetail(BackgroundTaskOutput o)
    {
        return new TaskOutputToolTaskDetail
        {
            TaskId = o.TaskId,
            TaskType = o.TaskType,
            Status = o.Status,
            Description = o.Description ?? "",
            Output = o.Output ?? "",
            ExitCode = o.ExitCode,
            Error = o.Error
        };
    }

    public AITool ToAITool()
    {
        Func<TaskOutputToolParams, TaskOutputToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
