using System.Globalization;
using System.Text.Json;
using A2A;
using Agw.Projects;
using Agw.Shared.Exceptions;
using AgwTaskProjection = Agw.Shared.Contracts.Projects.TaskProjection;

namespace Agw.A2A;

public class TaskStore : ITaskStore
{
    private const string SnapshotRecordType = "a2a-task-snapshot";
    private const string RecordTypeMetadataKey = "recordType";
    private const string SnapshotMetadataKey = "agentTask";
    private const string SystemUser = "a2a";

    private readonly IRepository<ProjectConversation> _contextRepository;
    private readonly IRepository<ProjectConversationChatHistory> _recordRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public TaskStore(
        IRepository<ProjectConversation> contextRepository,
        IRepository<ProjectConversationChatHistory> recordRepository,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider
    )
    {
        _contextRepository = contextRepository;
        _recordRepository = recordRepository;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var taskGuid = ParseRequiredTaskId(taskId);
        var task = await GetProjectedTaskAsync(taskGuid);
        if (task == null || task.ProjectId != ProjectDefaults.A2AId)
        {
            return;
        }

        var records = await _recordRepository.ListAsync(record => record.TaskId == taskGuid);
        foreach (var record in records)
        {
            _recordRepository.Remove(record);
        }

        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(taskId, out var taskGuid))
        {
            return null;
        }

        var task = await GetProjectedTaskAsync(taskGuid);
        if (task == null || task.ProjectId != ProjectDefaults.A2AId)
        {
            return null;
        }

        var records = await _recordRepository.ListAsync(record => record.TaskId == taskGuid);
        var snapshot = TryReadSnapshot(records);
        var agentTask = snapshot ?? BuildFallbackTask(task);
        return BuildTaskResult(agentTask, null, includeArtifacts: true);
    }

    public async Task<ListTasksResponse> ListTasksAsync(
        ListTasksRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var contexts = await _contextRepository.ListAsync(context => context.ProjectId == ProjectDefaults.A2AId);
        var filteredContexts = string.IsNullOrWhiteSpace(request.ContextId)
            ? contexts
            : contexts.Where(context => context.ContextId == request.ContextId.Trim()).ToList();
        var contextIds = filteredContexts.Select(context => context.Id).ToHashSet();
        var allRecords = await _recordRepository.ListAsync(record => contextIds.Contains(record.ConversationId));
        var contextById = filteredContexts.ToDictionary(context => context.Id);

        var persistedTasks = new List<AgentTask>();
        foreach (var recordGroup in allRecords.GroupBy(record => record.TaskId))
        {
            var records = recordGroup.ToList();
            var task = TaskExecutionMapper.ToTask(contextById[records[0].ConversationId], records);
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
        var pageSize = requestedPageSize <= 0 ? (totalSize == 0 ? 0 : totalSize) : requestedPageSize;
        var offset = ParsePageToken(request.PageToken);

        var page = orderedTasks
            .Skip(offset)
            .Take(pageSize)
            .Select(task => BuildTaskResult(task, request.HistoryLength, request.IncludeArtifacts ?? true))
            .ToList();

        var nextOffset = offset + page.Count;
        return new ListTasksResponse
        {
            Tasks = page,
            PageSize = page.Count,
            TotalSize = totalSize,
            NextPageToken = nextOffset < totalSize ? nextOffset.ToString(CultureInfo.InvariantCulture) : string.Empty,
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

        var now = _timeProvider.GetUtcNow();
        var statusTimestampUtc = task.Status?.Timestamp ?? now;
        var records = await _recordRepository.ListAsync(record => record.TaskId == taskGuid);
        var existingContext =
            records.Count == 0 ? null : await _contextRepository.GetByIdAsync(records[0].ConversationId);
        var existingTask =
            existingContext == null || records.Count == 0 ? null : TaskExecutionMapper.ToTask(existingContext, records);
        if (existingTask != null && existingTask.ProjectId != ProjectDefaults.A2AId)
        {
            throw new AgwException(ErrorCodes.A2ATaskIdAlreadyUsed);
        }

        var coarseStatus = ToTaskExecutionStatus(task.Status?.State ?? TaskState.Unspecified);
        var firstUserText = ExtractFirstUserText(task);
        var statusMessageText = ExtractMessageText(task.Status?.Message);

        if (existingTask == null)
        {
            existingContext = new ProjectConversation
            {
                Id = Guid.CreateVersion7(),
                ProjectId = ProjectDefaults.A2AId,
                ContextId = string.IsNullOrWhiteSpace(task.ContextId) ? taskGuid.Normalize() : task.ContextId.Trim(),
                Title = BuildTitle(firstUserText),
                CreateBy = SystemUser,
                CreateTime = statusTimestampUtc,
                UpdateBy = SystemUser,
                UpdateTime = statusTimestampUtc,
            };

            await _contextRepository.AddAsync(existingContext);
        }
        else
        {
            existingContext!.ContextId = string.IsNullOrWhiteSpace(task.ContextId)
                ? existingContext.ContextId
                : task.ContextId.Trim();
            existingContext.Title = BuildTitle(firstUserText, existingContext.Title);
            existingContext.UpdateBy = SystemUser;
            existingContext.UpdateTime = statusTimestampUtc;
            _contextRepository.Update(existingContext);
        }

        foreach (var record in records)
        {
            _recordRepository.Remove(record);
        }

        await _recordRepository.AddAsync(
            new ProjectConversationChatHistory
            {
                Id = Guid.CreateVersion7(),
                ConversationId = existingContext!.Id,
                TaskId = taskGuid,
                JobId = null,
                Status = coarseStatus,
                FinishedTime = IsTerminal(coarseStatus) ? statusTimestampUtc : null,
                TaskErrorMessage = statusMessageText,
                ConversationSequence = 0,
                AgentName = task.Status?.Message?.Role == Role.Agent ? SystemUser : null,
                Metadata = CreateSnapshotMetadata(task),
                Error = statusMessageText,
                CreateTime = statusTimestampUtc,
                UpdateTime = statusTimestampUtc,
            }
        );

        await _unitOfWork.SaveChangesAsync();
    }

    private async Task<AgwTaskProjection?> GetProjectedTaskAsync(Guid taskId)
    {
        var records = await _recordRepository.ListAsync(record => record.TaskId == taskId);
        if (records.Count == 0)
        {
            return null;
        }

        var context = await _contextRepository.GetByIdAsync(records[0].ConversationId);
        return context == null ? null : TaskExecutionMapper.ToTask(context, records);
    }

    private static Dictionary<string, JsonElement> CreateSnapshotMetadata(AgentTask task) =>
        new()
        {
            [RecordTypeMetadataKey] = JsonSerializer.SerializeToElement(SnapshotRecordType),
            [SnapshotMetadataKey] = JsonSerializer.SerializeToElement(task),
        };

    private static AgentTask? TryReadSnapshot(IEnumerable<ProjectConversationChatHistory> records)
    {
        foreach (var record in records)
        {
            if (record.Metadata == null)
            {
                continue;
            }

            if (
                !record.Metadata.TryGetValue(RecordTypeMetadataKey, out var recordType)
                || !string.Equals(recordType.GetString(), SnapshotRecordType, StringComparison.Ordinal)
            )
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

    private static AgentTask BuildFallbackTask(AgwTaskProjection task)
    {
        var statusMessageText = string.IsNullOrWhiteSpace(task.ErrorMessage) ? null : task.ErrorMessage.Trim();

        return new AgentTask
        {
            Id = task.TaskId.Normalize(),
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
                        MessageId = Guid.CreateVersion7().ToString("N"),
                        ContextId = task.ContextId,
                        TaskId = task.TaskId.Normalize(),
                        Parts = [Part.FromText(statusMessageText)],
                    },
            },
            History = [],
            Artifacts = [],
            Metadata = new Dictionary<string, JsonElement>(),
        };
    }

    private static AgentTask BuildTaskResult(AgentTask task, int? historyLength, bool includeArtifacts)
    {
        var projected = DeepClone(task);

        projected.History ??= [];
        projected.Artifacts ??= [];
        projected.Metadata ??= new Dictionary<string, JsonElement>();

        if (historyLength.HasValue && historyLength.Value >= 0 && projected.History.Count > historyLength.Value)
        {
            projected.History = projected
                .History.Skip(Math.Max(0, projected.History.Count - historyLength.Value))
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
        requestedAfter == null || (task.Status?.Timestamp != null && task.Status.Timestamp > requestedAfter.Value);

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
        task
            .History?.FirstOrDefault(message => message.Role == Role.User)
            ?.Parts?.Select(part => part.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
            ?.Trim();

    private static string? ExtractMessageText(Message? message) =>
        message?.Parts?.Select(part => part.Text).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))?.Trim();

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

    private static TaskExecutionStatus ToTaskExecutionStatus(TaskState taskState) =>
        taskState switch
        {
            TaskState.Working or TaskState.InputRequired => TaskExecutionStatus.Running,
            TaskState.Completed => TaskExecutionStatus.Succeeded,
            TaskState.Failed or TaskState.Rejected or TaskState.AuthRequired => TaskExecutionStatus.Failed,
            TaskState.Canceled => TaskExecutionStatus.Canceled,
            _ => TaskExecutionStatus.Pending,
        };

    private static TaskState ToA2ATaskState(TaskExecutionStatus taskStatus) =>
        taskStatus switch
        {
            TaskExecutionStatus.Running => TaskState.Working,
            TaskExecutionStatus.Succeeded => TaskState.Completed,
            TaskExecutionStatus.Failed => TaskState.Failed,
            TaskExecutionStatus.Canceled => TaskState.Canceled,
            _ => TaskState.Submitted,
        };

    private static bool IsTerminal(TaskExecutionStatus status) =>
        status is TaskExecutionStatus.Succeeded or TaskExecutionStatus.Failed or TaskExecutionStatus.Canceled;
}
