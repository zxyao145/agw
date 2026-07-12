using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Todo;

public class TaskUpdateToolParams
{
    [Description(
        """
        The ID of the task to update.
        """
    )]
    public string TaskId { get; set; } = "";

    [Description(
        """
        New subject for the task.
        """
    )]
    public string? Subject { get; set; }

    [Description(
        """
        New description for the task.
        """
    )]
    public string? Description { get; set; }

    [Description(
        """
        Present continuous form shown in spinner when in_progress (e.g., "Running tests").
        """
    )]
    public string? ActiveForm { get; set; }

    [Description(
        """
        New status for the task. One of: 'pending', 'in_progress', 'completed', or the special value 'deleted' to permanently remove the task.
        """
    )]
    public string? Status { get; set; }

    [Description(
        """
        New owner for the task (agent name).
        """
    )]
    public string? Owner { get; set; }

    [Description(
        """
        Task IDs that this task blocks (this task must complete before they can start).
        """
    )]
    public List<string>? AddBlocks { get; set; }

    [Description(
        """
        Task IDs that block this task (they must complete before this one can start).
        """
    )]
    public List<string>? AddBlockedBy { get; set; }

    [Description(
        """
        Metadata keys to merge into the task. Set a key to null to delete it.
        """
    )]
    public Dictionary<string, object?>? Metadata { get; set; }
}

public class TaskUpdateToolStatusChange
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public class TaskUpdateToolResult
{
    public bool Success { get; set; }
    public string TaskId { get; set; } = "";
    public List<string> UpdatedFields { get; set; } = [];
    public string? Error { get; set; }
    public TaskUpdateToolStatusChange? StatusChange { get; set; }
}

internal class TaskUpdateTool : IAgwTool
{
    private static readonly HashSet<string> _validStatuses =
    [
        "pending", "in_progress", "completed", "deleted"
    ];

    public string Name => "task_update";

    public string Category => "Todo";

    [Description(
        """
        Use this tool to update a task in the task list.

        ## When to Use This Tool

        Mark tasks as resolved:
        - When you have completed the work described in a task
        - When a task is no longer needed or has been superseded
        - IMPORTANT: Always mark your assigned tasks as resolved when you finish them

        Delete tasks:
        - When a task is no longer relevant or was created in error
        - Setting status to `deleted` permanently removes the task

        Update task details:
        - When requirements change or become clearer
        - When establishing dependencies between tasks

        ## Fields You Can Update

        - Status: 'pending', 'in_progress', 'completed', or 'deleted' (special: permanently removes)
        - Subject: Change the task title
        - Description: Change the task description
        - ActiveForm: Present continuous form for spinner
        - Owner: Change the task owner
        - Metadata: Merge keys into metadata (set key to null to delete)
        - AddBlocks: Mark tasks that cannot start until this one completes
        - AddBlockedBy: Mark tasks that must complete before this one can start

        ## Status Workflow

        Status progresses: pending -> in_progress -> completed.
        Use 'deleted' to permanently remove a task.

        ## Tips

        - Only mark a task as completed when you have FULLY accomplished it
        - If blocked, keep as in_progress and create a new task describing the blocker
        - Never mark as completed if tests are failing or implementation is partial
        """
    )]
    public TaskUpdateToolResult Execute(TaskUpdateToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.TaskId))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "TaskId is required.");
        }

        var existing = TodoTaskStore.Get(toolParams.TaskId);
        if (existing is null)
        {
            // Mirror TS behaviour: return non-error result so siblings aren't cancelled.
            return new TaskUpdateToolResult
            {
                Success = false,
                TaskId = toolParams.TaskId,
                Error = "Task not found"
            };
        }

        // Validate status value if provided.
        if (toolParams.Status is not null && !_validStatuses.Contains(toolParams.Status))
        {
            return new TaskUpdateToolResult
            {
                Success = false,
                TaskId = toolParams.TaskId,
                Error = $"Invalid status '{toolParams.Status}'. Must be one of: pending, in_progress, completed, deleted."
            };
        }

        // Deletion path: short-circuits all other updates.
        if (toolParams.Status == "deleted")
        {
            var deleted = TodoTaskStore.Delete(toolParams.TaskId);
            return new TaskUpdateToolResult
            {
                Success = deleted,
                TaskId = toolParams.TaskId,
                UpdatedFields = deleted ? ["deleted"] : [],
                Error = deleted ? null : "Failed to delete task",
                StatusChange = deleted
                    ? new TaskUpdateToolStatusChange { From = existing.Status, To = "deleted" }
                    : null
            };
        }

        var updatedFields = new List<string>();
        string? oldStatus = null;
        string? newStatus = null;

        var ok = TodoTaskStore.Update(toolParams.TaskId, t =>
        {
            if (toolParams.Subject is not null && toolParams.Subject != t.Subject)
            {
                t.Subject = toolParams.Subject;
                updatedFields.Add("subject");
            }

            if (toolParams.Description is not null && toolParams.Description != t.Description)
            {
                t.Description = toolParams.Description;
                updatedFields.Add("description");
            }

            if (toolParams.ActiveForm is not null && toolParams.ActiveForm != t.ActiveForm)
            {
                t.ActiveForm = toolParams.ActiveForm;
                updatedFields.Add("activeForm");
            }

            if (toolParams.Owner is not null && toolParams.Owner != t.Owner)
            {
                t.Owner = toolParams.Owner;
                updatedFields.Add("owner");
            }

            if (toolParams.Metadata is not null)
            {
                var merged = t.Metadata is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>(t.Metadata);
                foreach (var (k, v) in toolParams.Metadata)
                {
                    if (v is null) merged.Remove(k);
                    else merged[k] = v;
                }
                t.Metadata = merged;
                updatedFields.Add("metadata");
            }

            if (toolParams.Status is not null && toolParams.Status != t.Status)
            {
                oldStatus = t.Status;
                newStatus = toolParams.Status;
                t.Status = toolParams.Status;
                updatedFields.Add("status");
            }
        });

        if (!ok)
        {
            return new TaskUpdateToolResult
            {
                Success = false,
                TaskId = toolParams.TaskId,
                Error = "Task not found"
            };
        }

        // Block dependencies. addBlocks: this task blocks the targets.
        if (toolParams.AddBlocks is { Count: > 0 })
        {
            var addedAny = false;
            foreach (var blockId in toolParams.AddBlocks)
            {
                if (string.IsNullOrWhiteSpace(blockId)) continue;
                if (existing.Blocks.Contains(blockId)) continue;
                if (TodoTaskStore.AddBlock(toolParams.TaskId, blockId)) addedAny = true;
            }
            if (addedAny) updatedFields.Add("blocks");
        }

        // addBlockedBy: each blocker blocks this task (reversed argument order).
        if (toolParams.AddBlockedBy is { Count: > 0 })
        {
            var addedAny = false;
            foreach (var blockerId in toolParams.AddBlockedBy)
            {
                if (string.IsNullOrWhiteSpace(blockerId)) continue;
                if (existing.BlockedBy.Contains(blockerId)) continue;
                if (TodoTaskStore.AddBlock(blockerId, toolParams.TaskId)) addedAny = true;
            }
            if (addedAny) updatedFields.Add("blockedBy");
        }

        return new TaskUpdateToolResult
        {
            Success = true,
            TaskId = toolParams.TaskId,
            UpdatedFields = updatedFields,
            StatusChange = newStatus is not null
                ? new TaskUpdateToolStatusChange { From = oldStatus ?? existing.Status, To = newStatus }
                : null
        };
    }

    public AITool ToAITool()
    {
        Func<TaskUpdateToolParams, TaskUpdateToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
