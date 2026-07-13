using System.Collections.Concurrent;

namespace Agw.Tools.Impl.Todo;

/// <summary>
/// TailStatus for the TailOutput tool: describes the state of task output retrieval.
/// </summary>
public enum OutputRetrievalStatus
{
    Success,
    Timeout,
    NotReady
}

/// <summary>
/// In-memory task model matching TS TaskItem shape.
/// Persisted in a static ConcurrentDictionary (no DB dependency).
/// </summary>
public class TodoTaskItem
{
    public string Id { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? ActiveForm { get; set; }
    public string? Owner { get; set; }
    public List<string> Blocks { get; set; } = [];
    public List<string> BlockedBy { get; set; } = [];
    public Dictionary<string, object?>? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = TimeProvider.System.GetUtcNow();
    public DateTimeOffset UpdatedAt { get; set; } = TimeProvider.System.GetUtcNow();
}

/// <summary>
/// Simplified output model for background tasks.
/// </summary>
public class BackgroundTaskOutput
{
    public string TaskId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Description { get; set; }
    public string? Output { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Singleton in-memory task store. Replaces TS utils/tasks.ts file-based persistence.
/// Thread-safe via ConcurrentDictionary.
/// </summary>
internal static class TodoTaskStore
{
    private static readonly ConcurrentDictionary<string, TodoTaskItem> _tasks = new();
    private static readonly ConcurrentDictionary<string, BackgroundTaskOutput> _backgroundOutputs = new();
    private static int _nextId = 1;

    /// <summary>
    /// Internal tasks (not shown in TaskList).
    /// </summary>
    private static readonly HashSet<string> _internalTaskIds = new();

    public static TodoTaskItem Create(string subject, string description, string? activeForm,
        Dictionary<string, object?>? metadata)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        var now = TimeProvider.System.GetUtcNow();
        var task = new TodoTaskItem
        {
            Id = id,
            Subject = subject,
            Description = description,
            ActiveForm = activeForm,
            Status = "pending",
            Metadata = metadata,
            CreatedAt = now,
            UpdatedAt = now
        };
        _tasks[id] = task;
        return task;
    }

    public static TodoTaskItem? Get(string id)
    {
        _tasks.TryGetValue(id, out var task);
        return task;
    }

    public static List<TodoTaskItem> List()
    {
        return _tasks.Values
            .Where(t => !_internalTaskIds.Contains(t.Id))
            .OrderBy(t => int.TryParse(t.Id, out var n) ? n : 0)
            .ToList();
    }

    public static bool Update(string id, Action<TodoTaskItem> updater)
    {
        if (!_tasks.TryGetValue(id, out var task)) return false;
        updater(task);
        task.UpdatedAt = TimeProvider.System.GetUtcNow();
        return true;
    }

    public static bool Delete(string id)
    {
        return _tasks.TryRemove(id, out _);
    }

    public static void SetInternal(string id, bool isInternal = true)
    {
        if (isInternal) _ = _internalTaskIds.Add(id);
        else _ = _internalTaskIds.Remove(id);
    }

    /// <summary>
    /// Creates a blocking relationship: taskId blocks blockerId.
    /// </summary>
    public static bool AddBlock(string taskId, string blockerId)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return false;
        if (!_tasks.TryGetValue(blockerId, out var blocker)) return false;
        if (!task.Blocks.Contains(blockerId)) task.Blocks.Add(blockerId);
        if (!blocker.BlockedBy.Contains(taskId)) blocker.BlockedBy.Add(taskId);
        return true;
    }

    // Background task output (for TaskStop / TaskOutput)
    public static void RecordBackgroundOutput(BackgroundTaskOutput output)
    {
        _backgroundOutputs[output.TaskId] = output;
    }

    public static BackgroundTaskOutput? GetBackgroundOutput(string taskId)
    {
        _backgroundOutputs.TryGetValue(taskId, out var output);
        return output;
    }

    public static bool UpdateBackgroundOutput(string taskId, Action<BackgroundTaskOutput> updater)
    {
        if (!_backgroundOutputs.TryGetValue(taskId, out var output)) return false;
        updater(output);
        return true;
    }
}
