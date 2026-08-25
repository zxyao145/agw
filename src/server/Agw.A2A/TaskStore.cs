using System.Globalization;
using System.Text.Json;
using A2A;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Exceptions;

namespace Agw.A2A;

public class TaskStore : ITaskStore
{
    private readonly IExternalTaskSnapshotStore _snapshots;
    private readonly TimeProvider _timeProvider;

    public TaskStore(IExternalTaskSnapshotStore snapshots, TimeProvider timeProvider)
    {
        _snapshots = snapshots;
        _timeProvider = timeProvider;
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await _snapshots
            .DeleteAsync(ProjectDefaults.A2AId, ParseRequiredTaskId(taskId), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(taskId, out var taskGuid))
        {
            return null;
        }

        var snapshot = await _snapshots
            .GetAsync(ProjectDefaults.A2AId, taskGuid, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot == null)
        {
            return null;
        }

        var agentTask = ReadPayload(snapshot.Payload) ?? BuildFallbackTask(snapshot.Task);
        return BuildTaskResult(agentTask, null, includeArtifacts: true);
    }

    public async Task<ListTasksResponse> ListTasksAsync(
        ListTasksRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var snapshots = await _snapshots
            .ListAsync(ProjectDefaults.A2AId, request.ContextId, cancellationToken)
            .ConfigureAwait(false);
        var persistedTasks = snapshots
            .Select(snapshot => ReadPayload(snapshot.Payload) ?? BuildFallbackTask(snapshot.Task))
            .Where(task => MatchesStatus(task, request.Status))
            .Where(task => MatchesStatusTimestamp(task, request.StatusTimestampAfter))
            .OrderByDescending(task => task.Status?.Timestamp ?? DateTimeOffset.MinValue)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .ToList();

        var totalSize = persistedTasks.Count;
        var requestedPageSize = request.PageSize.GetValueOrDefault(totalSize == 0 ? 0 : totalSize);
        var pageSize = requestedPageSize <= 0 ? (totalSize == 0 ? 0 : totalSize) : requestedPageSize;
        var offset = ParsePageToken(request.PageToken);
        var page = persistedTasks
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
        var statusTimestamp = task.Status?.Timestamp ?? now;
        var contextId = string.IsNullOrWhiteSpace(task.ContextId) ? taskGuid.ToString("D") : task.ContextId.Trim();
        var errorMessage = ExtractMessageText(task.Status?.Message);
        var existing = await _snapshots
            .GetAsync(ProjectDefaults.A2AId, taskGuid, cancellationToken)
            .ConfigureAwait(false);
        var result = await _snapshots
            .SaveAsync(
                new SaveExternalTaskSnapshotRequest(
                    ProjectDefaults.A2AId,
                    taskGuid,
                    contextId,
                    BuildTitle(ExtractFirstUserText(task), existing?.Task.Title),
                    ToProjectTaskStatus(task.Status?.State ?? TaskState.Unspecified),
                    errorMessage,
                    statusTimestamp,
                    task.Status?.Message?.Role == Role.Agent ? "a2a" : null,
                    JsonSerializer.SerializeToElement(task)
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (result == ExternalTaskSaveResult.TaskIdConflict)
        {
            throw new AgwException(ErrorCodes.A2ATaskIdAlreadyUsed);
        }
    }

    private static AgentTask? ReadPayload(JsonElement? payload) => payload?.Deserialize<AgentTask>();

    private static AgentTask BuildFallbackTask(ProjectTaskSnapshot task)
    {
        var statusMessageText = string.IsNullOrWhiteSpace(task.ErrorMessage) ? null : task.ErrorMessage.Trim();
        return new AgentTask
        {
            Id = task.TaskId.ToString("D"),
            ContextId = task.ContextId,
            Status = new global::A2A.TaskStatus
            {
                State = ToA2ATaskState(task.Status),
                Timestamp = task.UpdatedAt ?? task.FinishedAt ?? task.CreatedAt,
                Message = string.IsNullOrWhiteSpace(statusMessageText)
                    ? null
                    : new Message
                    {
                        Role = Role.Agent,
                        MessageId = Guid.CreateVersion7().ToString("N"),
                        ContextId = task.ContextId,
                        TaskId = task.TaskId.ToString("D"),
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

    private static Guid ParseRequiredTaskId(string taskId) =>
        Guid.TryParse(taskId, out var taskGuid) ? taskGuid : throw new AgwException(ErrorCodes.A2ATaskIdMustBeGuid);

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

    private static ProjectTaskStatus ToProjectTaskStatus(TaskState taskState) =>
        taskState switch
        {
            TaskState.Working or TaskState.InputRequired => ProjectTaskStatus.Running,
            TaskState.Completed => ProjectTaskStatus.Succeeded,
            TaskState.Failed or TaskState.Rejected or TaskState.AuthRequired => ProjectTaskStatus.Failed,
            TaskState.Canceled => ProjectTaskStatus.Canceled,
            _ => ProjectTaskStatus.Pending,
        };

    private static TaskState ToA2ATaskState(ProjectTaskStatus taskStatus) =>
        taskStatus switch
        {
            ProjectTaskStatus.Running => TaskState.Working,
            ProjectTaskStatus.Succeeded => TaskState.Completed,
            ProjectTaskStatus.Failed => TaskState.Failed,
            ProjectTaskStatus.Canceled => TaskState.Canceled,
            _ => TaskState.Submitted,
        };
}
