using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Todo;

public class TaskCreateToolParams
{
    [Description(
        """
        A brief title for the task.
        """
    )]
    public string Subject { get; set; } = "";

    [Description(
        """
        What needs to be done.
        """
    )]
    public string Description { get; set; } = "";

    [Description(
        """
        Present continuous form shown in spinner when in_progress (e.g., "Running tests").
        """
    )]
    public string? ActiveForm { get; set; }

    [Description(
        """
        Arbitrary metadata to attach to the task.
        """
    )]
    public Dictionary<string, object?>? Metadata { get; set; }
}

public class TaskCreateToolResult
{
    public string Id { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";
}

internal class TaskCreateTool : IAgwTool
{
    public string Name => "task_create";

    public string Category => "Todo";

    [Description(
        """
        Use this tool to create a structured task list for your current coding session. This helps you track progress, organize complex tasks, and demonstrate thoroughness to the user.

        ## When to Use This Tool

        Use this tool proactively in these scenarios:
        - Complex multi-step tasks - When a task requires 3 or more distinct steps or actions
        - Non-trivial and complex tasks - Tasks that require careful planning or multiple operations
        - User explicitly requests todo list
        - User provides multiple tasks (numbered or comma-separated)
        - After receiving new instructions - Immediately capture user requirements as tasks
        - When you start working on a task - Mark it as in_progress BEFORE beginning work
        - After completing a task - Mark it as completed and add any new follow-up tasks discovered during implementation

        ## When NOT to Use This Tool

        Skip using this tool when:
        - There is only a single, straightforward task
        - The task is trivial and tracking it provides no organizational benefit
        - The task can be completed in less than 3 trivial steps
        - The task is purely conversational or informational

        ## Task Fields

        - Subject: A brief, actionable title in imperative form (e.g., "Fix authentication bug in login flow")
        - Description: What needs to be done
        - ActiveForm (optional): Present continuous form shown when the task is in_progress

        All tasks are created with status `pending`.

        ## Tips

        - Create tasks with clear, specific subjects that describe the outcome
        - After creating tasks, use task_update to set up dependencies (blocks/blockedBy) if needed
        - Check task_list first to avoid creating duplicate tasks
        """
    )]
    public TaskCreateToolResult Execute(TaskCreateToolParams toolParams)
    {
        ArgumentNullException.ThrowIfNull(toolParams);

        if (string.IsNullOrWhiteSpace(toolParams.Subject))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Subject is required.");
        }

        var task = TodoTaskStore.Create(
            toolParams.Subject,
            toolParams.Description,
            toolParams.ActiveForm,
            toolParams.Metadata);

        return new TaskCreateToolResult
        {
            Id = task.Id,
            Subject = task.Subject,
            Message = $"Task #{task.Id} created successfully: {task.Subject}"
        };
    }

    public AITool ToAITool()
    {
        Func<TaskCreateToolParams, TaskCreateToolResult> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
