using System.Globalization;
using System.Text.Json;

using A2A;

using Agw.Shared.Exceptions;

namespace Agw.A2A;

public class TaskStore : ITaskStore
{
    private const string SnapshotRecordType = "a2a-task-snapshot";
    private const string RecordTypeMetadataKey = "recordType";
    private const string SnapshotMetadataKey = "agentTask";
    private const string SystemUser = "a2a";

    private readonly IRepository<ProjectTask> _taskRepository;
    private readonly IRepository<TaskRecord> _recordRepository;
    private readonly IUnitOfWork _unitOfWork;

    public TaskStore(
        IRepository<ProjectTask> taskRepository,
        IRepository<TaskRecord> recordRepository,
        IUnitOfWork unitOfWork)
    {
        _taskRepository = taskRepository;
        _recordRepository = recordRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var taskGuid = ParseRequiredTaskId(taskId);
        var task = await _taskRepository.GetByIdAsync(taskGuid);
        if (task == null || task.ProjectId != ProjectDefaults.A2AId)
        {
            return;
        }

        var records = await _recordRepository.ListAsync(record => record.TaskId == taskGuid);
        foreach (var record in records)
        {
            _recordRepository.Remove(record);
        }

        _taskRepository.Remove(task);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(taskId, out var taskGuid))
        {
            return null;
        }

        var task = await _taskRepository.GetByIdAsync(taskGuid);
        if (task == null || task.ProjectId != ProjectDefaults.A2AId)
        {
            return null;
        }

        var records = await _recordRepository.ListAsync(record => record.TaskId == taskGuid);
        var snapshot = TryReadSnapshot(records);
        var agentTask = snapshot ?? BuildFallbackTask(task);
        return ProjectTaskResult(agentTask, null, includeArtifacts: true);
    }

    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default)
    {
        var tasks = await _taskRepository.ListAsync(task => task.ProjectId == ProjectDefaults.A2AId);
        var filteredTasks = string.IsNullOrWhiteSpace(request.ContextId)
            ? tasks
            : tasks.Where(task => task.ContextId == request.ContextId.Trim()).ToList();

        var persistedTasks = new List<AgentTask>();
        foreach (var task in filteredTasks)
        {
            var records = await _recordRepository.ListAsync(record => record.TaskId == task.Id);
            var agentTask = TryReadSnapshot(records) ?? BuildFallbackTask(task);
            if (!MatchesStatus(agentTask, request.Status))
            {
                continue;
            }

            if (!MatchesStatusTimestamp(agentTask, request.StatusTimestampAfter))
            {
                continue;
            }

            persistedTasks.Add(agentTask);
        }

        var orderedTasks = persistedTasks
            .OrderByDescending(task => task.Status?.Timestamp ?? DateTimeOffset.MinValue)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .ToList();

        var totalSize = orderedTasks.Count;
        var requestedPageSize = request.PageSize.GetValueOrDefault(totalSize == 0 ? 0 : totalSize);
        var pageSize = requestedPageSize <= 0
            ? (totalSize == 0 ? 0 : totalSize)
            : requestedPageSize;
        var offset = ParsePageToken(request.PageToken);

        var page = orderedTasks
            .Skip(offset)
            .Take(pageSize)
            .Select(task => ProjectTaskResult(task, request.HistoryLength, request.IncludeArtifacts ?? true))
            .ToList();

        var nextOffset = offset + page.Count;
        return new ListTasksResponse
        {
            Tasks = page,
            PageSize = page.Count,
            TotalSize = totalSize,
            NextPageToken = nextOffset < totalSize ? nextOffset.ToString(CultureInfo.InvariantCulture) : string.Empty
        };
    }

    public async Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var taskGuid = ParseRequiredTaskId(taskId);
        if (!string.Equals(task.Id, taskId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgwException(ErrorCodes.TaskIdMismatch);
        }

        var now = DateTime.UtcNow;
        var statusTimestampUtc = task.Status?.Timestamp?.UtcDateTime ?? now;
        var existingTask = await _taskRepository.GetByIdAsync(taskGuid);
        if (existingTask != null && existingTask.ProjectId != ProjectDefaults.A2AId)
        {
            throw new AgwException(ErrorCodes.A2ATaskIdAlreadyUsed);
        }

        var coarseStatus = ToProjectTaskStatus(task.Status?.State ?? TaskState.Unspecified);
        var firstUserText = ExtractFirstUserText(task);
        var statusMessageText = ExtractMessageText(task.Status?.Message);

        if (existingTask == null)
        {
            existingTask = new ProjectTask
            {
                Id = taskGuid,
                ProjectId = ProjectDefaults.A2AId,
                ContextId = string.IsNullOrWhiteSpace(task.ContextId) ? taskGuid.Normalize() : task.ContextId.Trim(),
                Title = BuildTitle(firstUserText),
                Status = coarseStatus,
                ErrorMessage = statusMessageText,
                CreateBy = SystemUser,
                CreateTime = statusTimestampUtc,
                UpdateBy = SystemUser,
                UpdateTime = statusTimestampUtc,
                FinishedTime = IsTerminal(coarseStatus) ? statusTimestampUtc : null
            };

            await _taskRepository.AddAsync(existingTask);
        }
        else
        {
            existingTask.ContextId = string.IsNullOrWhiteSpace(task.ContextId) ? existingTask.ContextId : task.ContextId.Trim();
            existingTask.Title = BuildTitle(firstUserText, existingTask.Title);
            existingTask.Status = coarseStatus;
            existingTask.ErrorMessage = statusMessageText;
            existingTask.UpdateBy = SystemUser;
            existingTask.UpdateTime = statusTimestampUtc;
            existingTask.FinishedTime = IsTerminal(coarseStatus) ? statusTimestampUtc : null;
            _taskRepository.Update(existingTask);
        }

        var records = await _recordRepository.ListAsync(record => record.TaskId == taskGuid);
        foreach (var record in records)
        {
            _recordRepository.Remove(record);
        }

        await _recordRepository.AddAsync(new TaskRecord
        {
            Id = Guid.NewGuid(),
            TaskId = taskGuid,
            ConversationSequence = 0,
            AgentName = task.Status?.Message?.Role == Role.Agent ? SystemUser : null,
            Metadata = CreateSnapshotMetadata(task),
            Error = statusMessageText,
            CreateTime = statusTimestampUtc,
            UpdateTime = statusTimestampUtc
        });

        await _unitOfWork.SaveChangesAsync();
    }

    private static Dictionary<string, JsonElement> CreateSnapshotMetadata(AgentTask task) =>
        new()
        {
            [RecordTypeMetadataKey] = JsonSerializer.SerializeToElement(SnapshotRecordType),
            [SnapshotMetadataKey] = JsonSerializer.SerializeToElement(task)
        };

    private static AgentTask? TryReadSnapshot(IEnumerable<TaskRecord> records)
    {
        foreach (var record in records)
        {
            if (record.Metadata == null)
            {
                continue;
            }

            if (!record.Metadata.TryGetValue(RecordTypeMetadataKey, out var recordType)
                || !string.Equals(recordType.GetString(), SnapshotRecordType, StringComparison.Ordinal))
            {
                continue;
            }

            if (!record.Metadata.TryGetValue(SnapshotMetadataKey, out var snapshot))
            {
                continue;
            }

            return snapshot.Deserialize<AgentTask>();
        }

        return null;
    }

    private static AgentTask BuildFallbackTask(ProjectTask task)
    {
        var statusMessageText = string.IsNullOrWhiteSpace(task.ErrorMessage) ? null : task.ErrorMessage.Trim();

        return new AgentTask
        {
            Id = task.Id.Normalize(),
            ContextId = task.ContextId,
            Status = new global::A2A.TaskStatus
            {
                State = ToA2ATaskState(task.Status),
                Timestamp = task.UpdateTime ?? task.FinishedTime ?? task.CreateTime,
                Message = string.IsNullOrWhiteSpace(statusMessageText)
                    ? null
                    : new Message
                    {
                        Role = Role.Agent,
                        MessageId = Guid.NewGuid().ToString("N"),
                        ContextId = task.ContextId,
                        TaskId = task.Id.Normalize(),
                        Parts = [Part.FromText(statusMessageText)]
                    }
            },
            History = [],
            Artifacts = [],
            Metadata = new Dictionary<string, JsonElement>()
        };
    }

    private static AgentTask ProjectTaskResult(AgentTask task, int? historyLength, bool includeArtifacts)
    {
        var projected = DeepClone(task);

        projected.History ??= [];
        projected.Artifacts ??= [];
        projected.Metadata ??= new Dictionary<string, JsonElement>();

        if (historyLength.HasValue && historyLength.Value >= 0 && projected.History.Count > historyLength.Value)
        {
            projected.History = projected.History
                .Skip(Math.Max(0, projected.History.Count - historyLength.Value))
                .ToList();
        }

        if (!includeArtifacts)
        {
            projected.Artifacts = [];
        }

        return projected;
    }

    private static AgentTask DeepClone(AgentTask task) =>
        JsonSerializer.Deserialize<AgentTask>(JsonSerializer.Serialize(task))
        ?? throw new AgwException(ErrorCodes.A2ATaskSnapshotCloneFailed);

    private static bool MatchesStatus(AgentTask task, TaskState? requestedStatus) =>
        requestedStatus == null || task.Status?.State == requestedStatus.Value;

    private static bool MatchesStatusTimestamp(AgentTask task, DateTimeOffset? requestedAfter) =>
        requestedAfter == null
        || (task.Status?.Timestamp != null && task.Status.Timestamp > requestedAfter.Value);

    private static int ParsePageToken(string? pageToken)
    {
        if (string.IsNullOrWhiteSpace(pageToken))
        {
            return 0;
        }

        return int.TryParse(pageToken, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? offset
            : 0;
    }

    private static Guid ParseRequiredTaskId(string taskId)
    {
        if (!Guid.TryParse(taskId, out var taskGuid))
        {
            throw new AgwException(ErrorCodes.A2ATaskIdMustBeGuid);
        }

        return taskGuid;
    }

    private static string? ExtractFirstUserText(AgentTask task) =>
        task.History?
            .FirstOrDefault(message => message.Role == Role.User)
            ?.Parts?
            .Select(part => part.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?.Trim();

    private static string? ExtractMessageText(Message? message) =>
        message?.Parts?
            .Select(part => part.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?.Trim();

    private static string BuildTitle(string? text, string? fallback = null)
    {
        var source = string.IsNullOrWhiteSpace(text) ? fallback : text;
        if (string.IsNullOrWhiteSpace(source))
        {
            return "A2A Task";
        }

        var normalized = source.Trim();
        return normalized[..Math.Min(normalized.Length, 80)];
    }

    private static ProjectTaskStatus ToProjectTaskStatus(TaskState taskState) =>
        taskState switch
        {
            TaskState.Working or TaskState.InputRequired => ProjectTaskStatus.Running,
            TaskState.Completed => ProjectTaskStatus.Succeeded,
            TaskState.Failed or TaskState.Rejected or TaskState.AuthRequired => ProjectTaskStatus.Failed,
            TaskState.Canceled => ProjectTaskStatus.Canceled,
            _ => ProjectTaskStatus.Pending
        };

    private static TaskState ToA2ATaskState(ProjectTaskStatus taskStatus) =>
        taskStatus switch
        {
            ProjectTaskStatus.Running => TaskState.Working,
            ProjectTaskStatus.Succeeded => TaskState.Completed,
            ProjectTaskStatus.Failed => TaskState.Failed,
            ProjectTaskStatus.Canceled => TaskState.Canceled,
            _ => TaskState.Submitted
        };

    private static bool IsTerminal(ProjectTaskStatus status) =>
        status is ProjectTaskStatus.Succeeded or ProjectTaskStatus.Failed or ProjectTaskStatus.Canceled;
}
