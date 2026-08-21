using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Agents.Execution.Durable;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Execution.Agentflows;

/// <summary>
/// 持久化 Agentflow checkpoint occurrence，并以聊天序号作为唯一回滚边界。
/// </summary>
public sealed class AgentflowCheckpointStore
{
    private const string CheckpointMessageType = "agentflow-checkpoint";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;

    public AgentflowCheckpointStore(
        IServiceScopeFactory scopeFactory,
        IApplicationLock applicationLock,
        TimeProvider timeProvider
    )
    {
        _scopeFactory = scopeFactory;
        _applicationLock = applicationLock;
        _timeProvider = timeProvider;
    }

    internal async Task<RecordedAgentflowCheckpoint?> RecordAsync(
        Guid? sourceExecutionId,
        Guid projectId,
        Guid conversationId,
        string contextId,
        Guid taskId,
        Guid agentflowId,
        string userId,
        bool isDurable,
        string definitionFingerprint,
        DurableAgentflowCheckpoint checkpoint,
        IReadOnlyDictionary<string, string> markerNames,
        CancellationToken cancellationToken
    )
    {
        if (conversationId == Guid.Empty || markerNames.Count == 0)
        {
            return null;
        }
        userId = string.IsNullOrWhiteSpace(userId) ? Constants.AdminUserId : userId.Trim();

        var occurrenceId = CreateDeterministicGuid(
            sourceExecutionId ?? taskId,
            checkpoint.SessionId,
            checkpoint.CheckpointId
        );
        var markers = markerNames
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(
                (item, index) =>
                    new AgentflowCheckpointMarker(
                        item.Key,
                        item.Value,
                        CreateDeterministicGuid(occurrenceId, item.Key, index.ToString()).ToString("D")
                    )
            )
            .ToArray();
        var chatMessages = markers.Select(marker => CreateMessage(occurrenceId, marker)).ToArray();
        var messages = chatMessages.Select(item => item.ToAiMessage()).OfType<AgwMessage>().ToArray();

        await using var historyLock = await _applicationLock
            .AcquireAsync(GetHistoryLockName(projectId, contextId), cancellationToken)
            .ConfigureAwait(false);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        var existing = await dbContext
            .Set<AgentflowCheckpointRecord>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == occurrenceId, cancellationToken)
            .ConfigureAwait(false);
        if (existing != null)
        {
            if (!string.Equals(existing.UserId, userId, StringComparison.Ordinal))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Agentflow checkpoint owner does not match.");
            }
            return new RecordedAgentflowCheckpoint(ToSnapshot(existing), messages);
        }

        var conversationExists = await dbContext
            .Set<ProjectConversation>()
            .AsNoTracking()
            .AnyAsync(
                item => item.Id == conversationId && item.ProjectId == projectId && item.ContextId == contextId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!conversationExists)
        {
            return null;
        }

        await using var transaction = await dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var nextSequence =
            await dbContext
                .Set<ProjectConversationChatHistory>()
                .Where(item => item.ConversationId == conversationId)
                .Select(item => item.ConversationSequence)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? -1;
        var now = _timeProvider.GetUtcNow();
        foreach (var message in chatMessages)
        {
            nextSequence++;
            dbContext.Add(
                new ProjectConversationChatHistory
                {
                    Id = Guid.CreateVersion7(),
                    ConversationId = conversationId,
                    TaskId = taskId,
                    Status = TaskExecutionStatus.Succeeded,
                    AgentName = message.AuthorName,
                    ConversationSequence = nextSequence,
                    ConversationPayload = JsonSerializer.Serialize(message, JsonOptions),
                    CreateTime = now,
                    UpdateTime = now,
                }
            );
        }

        var record = new AgentflowCheckpointRecord
        {
            Id = occurrenceId,
            SourceExecutionId = sourceExecutionId,
            ProjectId = projectId,
            ProjectConversationId = conversationId,
            ContextId = contextId,
            TaskId = taskId,
            AgentflowId = agentflowId,
            UserId = userId,
            IsDurable = isDurable,
            BoundarySequence = nextSequence,
            DefinitionFingerprint = definitionFingerprint,
            MarkersJson = JsonSerializer.Serialize(markers, JsonOptions),
            CheckpointJson = DurableExecutionJson.Serialize(checkpoint),
            CreateBy = userId,
            CreateTime = now,
            UpdateBy = userId,
            UpdateTime = now,
        };
        dbContext.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RecordedAgentflowCheckpoint(ToSnapshot(record), messages);
    }

    internal async Task<IReadOnlyList<AgentflowCheckpointAvailability>> ListAsync(
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        IReadOnlySet<Guid>? inProcessOccurrences,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        var fingerprint = await AgentflowDefinitionFingerprint
            .CreateAsync(dbContext, agentflowId, cancellationToken)
            .ConfigureAwait(false);
        if (fingerprint == null)
        {
            return [];
        }

        var records = await dbContext
            .Set<AgentflowCheckpointRecord>()
            .AsNoTracking()
            .Where(item =>
                item.ProjectId == projectId
                && item.ContextId == contextId
                && item.AgentflowId == agentflowId
                && item.UserId == userId
            )
            .OrderBy(item => item.BoundarySequence)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return records
            .Select(record => new AgentflowCheckpointAvailability(
                record.Id,
                record.AgentflowId,
                record.BoundarySequence,
                string.Equals(record.DefinitionFingerprint, fingerprint, StringComparison.Ordinal)
                    && HasReadableCheckpoint(record)
                    && (record.IsDurable || inProcessOccurrences?.Contains(record.Id) == true),
                DeserializeMarkers(record.MarkersJson)
                    .Select(marker => new AgentflowCheckpointMarkerInfo(marker.NodeId, marker.Name, marker.MessageId))
                    .ToArray()
            ))
            .ToArray();
    }

    internal async Task<string?> GetDefinitionFingerprintAsync(Guid agentflowId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        return await AgentflowDefinitionFingerprint
            .CreateAsync(dbContext, agentflowId, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<AgentflowCheckpointSnapshot> PrepareInProcessResumeAsync(
        Guid occurrenceId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        CancellationToken cancellationToken
    ) =>
        await PrepareResumeAsync(
                occurrenceId,
                projectId,
                contextId,
                agentflowId,
                userId,
                resumeExecutionId: null,
                cancellationToken
            )
            .ConfigureAwait(false);

    internal async Task<AgentflowCheckpointSnapshot> PrepareDistributedResumeAsync(
        Guid occurrenceId,
        Guid resumeExecutionId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        CancellationToken cancellationToken
    ) =>
        await PrepareResumeAsync(
                occurrenceId,
                projectId,
                contextId,
                agentflowId,
                userId,
                resumeExecutionId,
                cancellationToken
            )
            .ConfigureAwait(false);

    internal async Task<Guid?> GetSourceExecutionIdAsync(
        Guid occurrenceId,
        string userId,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        return await dbContext
            .Set<AgentflowCheckpointRecord>()
            .AsNoTracking()
            .Where(item => item.Id == occurrenceId && item.UserId == userId)
            .Select(item => item.SourceExecutionId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AgentflowCheckpointSnapshot> PrepareResumeAsync(
        Guid occurrenceId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        Guid? resumeExecutionId,
        CancellationToken cancellationToken
    )
    {
        await using var historyLock = await _applicationLock
            .AcquireAsync(GetHistoryLockName(projectId, contextId), cancellationToken)
            .ConfigureAwait(false);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        await using var transaction = await dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var record =
            await dbContext
                .Set<AgentflowCheckpointRecord>()
                .SingleOrDefaultAsync(item => item.Id == occurrenceId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.InvalidParam, "Agentflow checkpoint was not found.");
        if (
            record.ProjectId != projectId
            || !string.Equals(record.ContextId, contextId, StringComparison.Ordinal)
            || record.AgentflowId != agentflowId
            || !string.Equals(record.UserId, userId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Agentflow checkpoint does not match the current conversation target."
            );
        }
        if (record.IsDurable != resumeExecutionId.HasValue)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                record.IsDurable
                    ? "The selected checkpoint requires distributed execution."
                    : "The selected checkpoint belongs to an in-process runtime."
            );
        }

        var fingerprint = await AgentflowDefinitionFingerprint
            .CreateAsync(dbContext, agentflowId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(record.DefinitionFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Agentflow definition changed after this checkpoint was created."
            );
        }

        // 在任何删除前验证快照可解密、可反序列化；失败时事务内不修改聊天历史。
        var snapshot = ToSnapshot(record);

        if (resumeExecutionId.HasValue)
        {
            var existingResume = await dbContext
                .Set<DurableExecutionRecord>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == resumeExecutionId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (existingResume != null)
            {
                var existingManifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
                    existingResume.ManifestJson,
                    "durable resume manifest"
                );
                if (existingResume.UserId == userId && existingManifest.ResumeCheckpointOccurrenceId == record.Id)
                {
                    return snapshot;
                }

                throw new AgwException(ErrorCodes.DurableExecutionConflict);
            }

            var activeExecutions = await dbContext
                .Set<DurableExecutionRecord>()
                .AsNoTracking()
                .Where(item =>
                    item.UserId == userId
                    && item.Status != DurableExecutionStatus.Completed
                    && item.Status != DurableExecutionStatus.Failed
                    && item.Status != DurableExecutionStatus.Interrupted
                )
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (
                activeExecutions.Any(item =>
                    DurableExecutionJson
                        .DeserializeRequired<DurableExecutionManifest>(item.ManifestJson, "durable execution manifest")
                        .Task.ProjectConversationId == record.ProjectConversationId
                )
            )
            {
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "Stop the active Agentflow execution before resuming a checkpoint."
                );
            }

            await RegisterResumeExecutionAsync(dbContext, record, resumeExecutionId.Value, userId, cancellationToken)
                .ConfigureAwait(false);
        }

        await dbContext
            .Set<ProjectConversationChatHistory>()
            .Where(item =>
                item.ConversationId == record.ProjectConversationId
                && item.ConversationSequence > record.BoundarySequence
            )
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await dbContext
            .Set<AgentflowCheckpointRecord>()
            .Where(item =>
                item.ProjectConversationId == record.ProjectConversationId
                && item.BoundarySequence > record.BoundarySequence
            )
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async Task RegisterResumeExecutionAsync(
        DbContext dbContext,
        AgentflowCheckpointRecord checkpointRecord,
        Guid resumeExecutionId,
        string userId,
        CancellationToken cancellationToken
    )
    {
        if (!checkpointRecord.IsDurable || !checkpointRecord.SourceExecutionId.HasValue)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The selected checkpoint is not a durable checkpoint.");
        }
        if (resumeExecutionId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "resumeExecutionId is required.");
        }

        var existing = await dbContext
            .Set<DurableExecutionRecord>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == resumeExecutionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing != null)
        {
            var existingManifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
                existing.ManifestJson,
                "durable resume manifest"
            );
            if (existing.UserId == userId && existingManifest.ResumeCheckpointOccurrenceId == checkpointRecord.Id)
            {
                return;
            }

            throw new AgwException(ErrorCodes.DurableExecutionConflict);
        }

        var source =
            await dbContext
                .Set<DurableExecutionRecord>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == checkpointRecord.SourceExecutionId.Value && item.UserId == userId,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        if (
            source.Status
            is not DurableExecutionStatus.Completed
                and not DurableExecutionStatus.Failed
                and not DurableExecutionStatus.Interrupted
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "Stop the active Agentflow execution before resuming a checkpoint."
            );
        }

        var manifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
            source.ManifestJson,
            "durable execution manifest"
        ) with
        {
            ExecutionId = resumeExecutionId,
            ResumeCheckpointOccurrenceId = checkpointRecord.Id,
            ResumeCheckpointNodeIds = DeserializeMarkers(checkpointRecord.MarkersJson)
                .Select(item => item.NodeId)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };
        var now = _timeProvider.GetUtcNow();
        dbContext.Add(
            new DurableExecutionRecord
            {
                Id = resumeExecutionId,
                UserId = userId,
                ManifestJson = DurableExecutionJson.Serialize(manifest),
                Status = DurableExecutionStatus.Resuming,
                SegmentIndex = 1,
                CheckpointJson = checkpointRecord.CheckpointJson,
                StateChangedAt = now,
                StateVersion = Guid.CreateVersion7(),
                CreateBy = userId,
                CreateTime = now,
                UpdateBy = userId,
                UpdateTime = now,
            }
        );
    }

    private static AgentflowCheckpointSnapshot ToSnapshot(AgentflowCheckpointRecord record) =>
        new(
            record.Id,
            record.SourceExecutionId,
            record.AgentflowId,
            record.BoundarySequence,
            record.DefinitionFingerprint,
            DeserializeMarkers(record.MarkersJson),
            DurableExecutionJson.DeserializeRequired<DurableAgentflowCheckpoint>(
                record.CheckpointJson,
                "agentflow checkpoint"
            )
        );

    private static AgentflowCheckpointMarker[] DeserializeMarkers(string value) =>
        JsonSerializer.Deserialize<AgentflowCheckpointMarker[]>(value, JsonOptions) ?? [];

    private static bool HasReadableCheckpoint(AgentflowCheckpointRecord record)
    {
        try
        {
            _ = ToSnapshot(record);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or AgwException)
        {
            return false;
        }
    }

    private static ChatMessage CreateMessage(Guid occurrenceId, AgentflowCheckpointMarker marker)
    {
        var properties = new AdditionalPropertiesDictionary
        {
            ["type"] = CheckpointMessageType,
            ["checkpointOccurrenceId"] = occurrenceId.ToString("D"),
            ["checkpointNodeId"] = marker.NodeId,
            ["checkpointName"] = marker.Name,
        };
        return new ChatMessage(ChatRole.Assistant, marker.Name)
        {
            MessageId = marker.MessageId,
            AuthorName = Constants.DefaultAgentAuthor,
            AdditionalProperties = properties,
        };
    }

    private static Guid CreateDeterministicGuid(Guid seed, params string[] values)
    {
        var input = string.Join("\u001f", new[] { seed.ToString("N") }.Concat(values));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string GetHistoryLockName(Guid projectId, string contextId) =>
        $"conversation-history:{projectId:D}:{contextId}";
}
