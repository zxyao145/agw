using System.Text.Json;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;

namespace Agw.Projects.Application.Facades;

public sealed class ExternalTaskSnapshotStore : IExternalTaskSnapshotStore
{
    private const string SnapshotRecordType = "a2a-task-snapshot";
    private const string RecordTypeMetadataKey = "recordType";
    private const string SnapshotMetadataKey = "agentTask";
    private const string SystemUser = "external";

    private readonly IRepository<ProjectConversation> _conversationRepository;
    private readonly IRepository<ProjectConversationChatHistory> _historyRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ExternalTaskSnapshotStore(
        IRepository<ProjectConversation> conversationRepository,
        IRepository<ProjectConversationChatHistory> historyRepository,
        IUnitOfWork unitOfWork
    )
    {
        _conversationRepository = conversationRepository;
        _historyRepository = historyRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<ExternalTaskSnapshot?> GetAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default
    )
    {
        var records = await _historyRepository.ListAsync(record => record.TaskId == taskId).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return null;
        }

        var conversation = await _conversationRepository.GetByIdAsync(records[0].ConversationId).ConfigureAwait(false);
        return conversation == null || conversation.ProjectId != projectId
            ? null
            : BuildSnapshot(conversation, records);
    }

    public async Task<IReadOnlyList<ExternalTaskSnapshot>> ListAsync(
        Guid projectId,
        string? contextId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedContextId = string.IsNullOrWhiteSpace(contextId) ? null : contextId.Trim();
        var conversations = await _conversationRepository
            .ListAsync(conversation =>
                conversation.ProjectId == projectId
                && (normalizedContextId == null || conversation.ContextId == normalizedContextId)
            )
            .ConfigureAwait(false);
        if (conversations.Count == 0)
        {
            return [];
        }

        var conversationById = conversations.ToDictionary(conversation => conversation.Id);
        var conversationIds = conversationById.Keys.ToHashSet();
        var histories = await _historyRepository
            .ListAsync(record => conversationIds.Contains(record.ConversationId))
            .ConfigureAwait(false);

        return histories
            .GroupBy(record => record.TaskId)
            .Where(group => conversationById.ContainsKey(group.First().ConversationId))
            .Select(group => BuildSnapshot(conversationById[group.First().ConversationId], group.ToList()))
            .OrderByDescending(snapshot => snapshot.Task.UpdatedAt ?? snapshot.Task.CreatedAt)
            .ThenBy(snapshot => snapshot.Task.TaskId)
            .ToArray();
    }

    public async Task<ExternalTaskSaveResult> SaveAsync(
        SaveExternalTaskSnapshotRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var records = await _historyRepository
            .ListAsync(record => record.TaskId == request.TaskId)
            .ConfigureAwait(false);
        ProjectConversation? conversation = null;
        if (records.Count > 0)
        {
            conversation = await _conversationRepository.GetByIdAsync(records[0].ConversationId).ConfigureAwait(false);
            if (conversation != null && conversation.ProjectId != request.ProjectId)
            {
                return ExternalTaskSaveResult.TaskIdConflict;
            }
        }

        conversation ??= await _conversationRepository
            .SingleOrDefaultAsync(
                item => item.ProjectId == request.ProjectId && item.ContextId == request.ContextId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (conversation == null)
        {
            conversation = new ProjectConversation
            {
                Id = Guid.CreateVersion7(),
                ProjectId = request.ProjectId,
                ContextId = request.ContextId,
                Title = request.Title,
                CreateBy = SystemUser,
                CreateTime = request.StatusTimestamp,
                UpdateBy = SystemUser,
                UpdateTime = request.StatusTimestamp,
            };
            await _conversationRepository.AddAsync(conversation).ConfigureAwait(false);
        }
        else
        {
            conversation.ContextId = request.ContextId;
            conversation.Title = request.Title;
            conversation.UpdateBy = SystemUser;
            conversation.UpdateTime = request.StatusTimestamp;
            _conversationRepository.Update(conversation);
        }

        foreach (var record in records)
        {
            _historyRepository.Remove(record);
        }

        await _historyRepository
            .AddAsync(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = conversation.Id,
                    TaskId = request.TaskId,
                    Status = ProjectTaskFacade.Map(request.Status),
                    FinishedTime = IsTerminal(request.Status) ? request.StatusTimestamp : null,
                    TaskErrorMessage = request.ErrorMessage,
                    ConversationSequence = 0,
                    AgentName = request.AgentName,
                    Metadata = new Dictionary<string, JsonElement>
                    {
                        [RecordTypeMetadataKey] = JsonSerializer.SerializeToElement(SnapshotRecordType),
                        [SnapshotMetadataKey] = request.Payload.Clone(),
                    },
                    Error = request.ErrorMessage,
                    CreateTime = request.StatusTimestamp,
                    UpdateTime = request.StatusTimestamp,
                }
            )
            .ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ExternalTaskSaveResult.Saved;
    }

    public async Task DeleteAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default)
    {
        var records = await _historyRepository.ListAsync(record => record.TaskId == taskId).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return;
        }

        var conversation = await _conversationRepository.GetByIdAsync(records[0].ConversationId).ConfigureAwait(false);
        if (conversation == null || conversation.ProjectId != projectId)
        {
            return;
        }

        foreach (var record in records)
        {
            _historyRepository.Remove(record);
        }
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ExternalTaskSnapshot BuildSnapshot(
        ProjectConversation conversation,
        IReadOnlyList<ProjectConversationChatHistory> records
    )
    {
        var task = ProjectTaskFacade.Map(TaskExecutionMapper.ToTask(conversation, records));
        JsonElement? payload = null;
        foreach (var record in records.OrderByDescending(record => record.UpdateTime ?? record.CreateTime))
        {
            if (
                record.Metadata?.TryGetValue(RecordTypeMetadataKey, out var recordType) == true
                && string.Equals(recordType.GetString(), SnapshotRecordType, StringComparison.Ordinal)
                && record.Metadata.TryGetValue(SnapshotMetadataKey, out var snapshot)
            )
            {
                payload = snapshot.Clone();
                break;
            }
        }

        return new ExternalTaskSnapshot(task, payload);
    }

    private static bool IsTerminal(ProjectTaskStatus status) =>
        status is ProjectTaskStatus.Succeeded or ProjectTaskStatus.Failed or ProjectTaskStatus.Canceled;
}
