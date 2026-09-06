using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application.Facades;

public sealed class ExternalTaskSnapshotStore : IExternalTaskSnapshotStore
{
    private const string SnapshotRecordType = "a2a-task-snapshot";
    private const string RecordTypeMetadataKey = "recordType";
    private const string SnapshotMetadataKey = "agentTask";
    private readonly IProjectsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public ExternalTaskSnapshotStore(IProjectsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public async Task<ExternalTaskSnapshot?> GetAsync(
        Guid projectId,
        Guid taskId,
        CancellationToken cancellationToken = default
    )
    {
        if (!await IsProjectVisibleAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var records = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => record.TaskId == taskId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (records.Count == 0)
        {
            return null;
        }

        var ownerUserId = ResolveOwnerUserId();
        var conversation = await _dbContext
            .ProjectConversations.AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.Id == records[0].ConversationId && item.ProjectId == projectId && item.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        return conversation == null || conversation.ProjectId != projectId
            ? null
            : BuildSnapshot(conversation, records.Where(record => record.ConversationId == conversation.Id).ToList());
    }

    public async Task<IReadOnlyList<ExternalTaskSnapshot>> ListAsync(
        Guid projectId,
        string? contextId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await IsProjectVisibleAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var normalizedContextId = string.IsNullOrWhiteSpace(contextId) ? null : contextId.Trim();
        var ownerUserId = ResolveOwnerUserId();
        var conversations = await _dbContext
            .ProjectConversations.AsNoTracking()
            .Where(conversation =>
                conversation.ProjectId == projectId
                && (normalizedContextId == null || conversation.ContextId == normalizedContextId)
                && conversation.CreateBy == ownerUserId
            )
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (conversations.Count == 0)
        {
            return [];
        }

        var conversationById = conversations.ToDictionary(conversation => conversation.Id);
        var conversationIds = conversationById.Keys.ToHashSet();
        var histories = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => conversationIds.Contains(record.ConversationId))
            .ToListAsync(cancellationToken)
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
        if (!await IsProjectVisibleAsync(request.ProjectId, cancellationToken).ConfigureAwait(false))
        {
            return ExternalTaskSaveResult.TaskIdConflict;
        }

        var records = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => record.TaskId == request.TaskId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ProjectConversation? conversation = null;
        if (records.Count > 0)
        {
            var ownerUserId = ResolveOwnerUserId();
            conversation = await _dbContext
                .ProjectConversations.SingleOrDefaultAsync(
                    item => item.Id == records[0].ConversationId && item.CreateBy == ownerUserId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (conversation == null)
            {
                return ExternalTaskSaveResult.TaskIdConflict;
            }
            if (conversation != null && conversation.ProjectId != request.ProjectId)
            {
                return ExternalTaskSaveResult.TaskIdConflict;
            }
        }

        var currentOwnerUserId = ResolveOwnerUserId();
        conversation ??= await _dbContext
            .ProjectConversations.SingleOrDefaultAsync(
                item =>
                    item.ProjectId == request.ProjectId
                    && item.ContextId == request.ContextId
                    && item.CreateBy == currentOwnerUserId,
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
                CreateBy = currentOwnerUserId,
                CreateTime = request.StatusTimestamp,
                UpdateBy = currentOwnerUserId,
                UpdateTime = request.StatusTimestamp,
            };
            await _dbContext.ProjectConversations.AddAsync(conversation, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            conversation.ContextId = request.ContextId;
            conversation.Title = request.Title;
            conversation.UpdateBy = currentOwnerUserId;
            conversation.UpdateTime = request.StatusTimestamp;
        }

        foreach (var record in records.Where(record => record.ConversationId == conversation.Id))
        {
            _dbContext.ProjectConversationChatHistories.Remove(record);
        }

        await _dbContext
            .ProjectConversationChatHistories.AddAsync(
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
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ExternalTaskSaveResult.Saved;
    }

    public async Task DeleteAsync(Guid projectId, Guid taskId, CancellationToken cancellationToken = default)
    {
        if (!await IsProjectVisibleAsync(projectId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var records = await _dbContext
            .ProjectConversationChatHistories.AsNoTracking()
            .Where(record => record.TaskId == taskId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (records.Count == 0)
        {
            return;
        }

        var ownerUserId = ResolveOwnerUserId();
        var conversation = await _dbContext
            .ProjectConversations.AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.Id == records[0].ConversationId && item.ProjectId == projectId && item.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (conversation == null || conversation.ProjectId != projectId)
        {
            return;
        }

        foreach (var record in records.Where(record => record.ConversationId == conversation.Id))
        {
            _dbContext.ProjectConversationChatHistories.Remove(record);
        }
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;

    private async Task<bool> IsProjectVisibleAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var ownerUserId = ResolveOwnerUserId();
        return await _dbContext
            .Projects.AsNoTracking()
            .AnyAsync(project => project.Id == projectId && project.CreateBy == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
    }
}
