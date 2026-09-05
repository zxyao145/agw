using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Durable;
using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
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
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

        userId = userId.Trim();
        if (
            !UserInfoUtil.IsContextActive
            || !string.Equals(UserInfoUtil.RequiredUserId, userId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

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

        await using var lifecycleLock = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(projectId), cancellationToken)
            .ConfigureAwait(false);
        await using var historyLock = await _applicationLock
            .AcquireAsync(GetHistoryLockName(projectId, contextId), cancellationToken)
            .ConfigureAwait(false);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IAgentflowCheckpointPersistence>();
        var existing = await persistence.FindCheckpointAsync(occurrenceId, cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            if (!string.Equals(existing.UserId, userId, StringComparison.Ordinal))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Agentflow checkpoint owner does not match.");
            }

            return new RecordedAgentflowCheckpoint(ToSnapshot(existing), messages);
        }

        if (
            !await persistence
                .ProjectConversationExistsAsync(projectId, conversationId, contextId, userId, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            return null;
        }

        return await persistence
            .ExecuteAsync(
                async (session, token) =>
                {
                    var nextSequence = await session
                        .GetLastConversationSequenceAsync(conversationId, token)
                        .ConfigureAwait(false);
                    var now = _timeProvider.GetUtcNow();
                    foreach (var message in chatMessages)
                    {
                        nextSequence++;
                        session.AddConversationHistory(
                            new AgentflowCheckpointHistoryWrite(
                                Guid.CreateVersion7(),
                                conversationId,
                                taskId,
                                message.AuthorName,
                                nextSequence,
                                JsonSerializer.Serialize(message, JsonOptions),
                                now
                            )
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
                    session.Agents.AgentflowCheckpoints.Add(record);
                    return new AgentflowCheckpointPersistenceResult<RecordedAgentflowCheckpoint?>(
                        new RecordedAgentflowCheckpoint(ToSnapshot(record), messages),
                        Commit: true
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);
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
        var dbContext = scope.ServiceProvider.GetRequiredService<IAgentsDbContext>();
        var fingerprint = await AgentflowDefinitionFingerprint
            .CreateAsync(dbContext, agentflowId, cancellationToken)
            .ConfigureAwait(false);
        if (fingerprint == null)
        {
            return [];
        }

        var records = await dbContext
            .AgentflowCheckpoints.AsNoTracking()
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
        var dbContext = scope.ServiceProvider.GetRequiredService<IAgentsDbContext>();
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
        var dbContext = scope.ServiceProvider.GetRequiredService<IAgentsDbContext>();
        return await dbContext
            .AgentflowCheckpoints.AsNoTracking()
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
        await using var lifecycleLock = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(projectId), cancellationToken)
            .ConfigureAwait(false);
        await using var historyLock = await _applicationLock
            .AcquireAsync(GetHistoryLockName(projectId, contextId), cancellationToken)
            .ConfigureAwait(false);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IAgentflowCheckpointPersistence>();
        if (resumeExecutionId.HasValue)
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<IAgentsDbContext>();
            var existingResume = await dbContext
                .DurableExecutions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == resumeExecutionId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (existingResume != null)
            {
                var validated = await LoadValidatedResumeCheckpointAsync(
                        dbContext,
                        occurrenceId,
                        projectId,
                        contextId,
                        agentflowId,
                        userId,
                        expectsDurable: true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                EnsureExistingResumeMatches(existingResume, validated.Record.Id, userId);
                return validated.Snapshot;
            }

            var target = await LoadValidatedResumeCheckpointAsync(
                    dbContext,
                    occurrenceId,
                    projectId,
                    contextId,
                    agentflowId,
                    userId,
                    expectsDurable: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await persistence.BackfillExecutionScopesAsync(cancellationToken).ConfigureAwait(false);
            if (
                await persistence
                    .RepairAndCheckActiveExecutionsAsync(
                        projectId,
                        target.Record.ProjectConversationId,
                        userId,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "Stop the active Agentflow execution before resuming a checkpoint."
                );
            }
        }

        return await persistence
            .ExecuteAsync(
                async (session, token) =>
                {
                    var validated = await LoadValidatedResumeCheckpointAsync(
                            session.Agents,
                            occurrenceId,
                            projectId,
                            contextId,
                            agentflowId,
                            userId,
                            resumeExecutionId.HasValue,
                            token
                        )
                        .ConfigureAwait(false);
                    var record = validated.Record;
                    var snapshot = validated.Snapshot;

                    if (resumeExecutionId.HasValue)
                    {
                        var existingResume = await session
                            .Agents.DurableExecutions.AsNoTracking()
                            .SingleOrDefaultAsync(item => item.Id == resumeExecutionId.Value, token)
                            .ConfigureAwait(false);
                        if (existingResume != null)
                        {
                            EnsureExistingResumeMatches(existingResume, record.Id, userId);
                            return new AgentflowCheckpointPersistenceResult<AgentflowCheckpointSnapshot>(
                                snapshot,
                                Commit: false
                            );
                        }

                        if (
                            await session
                                .Agents.DurableExecutions.InConversation(
                                    projectId,
                                    record.ProjectConversationId,
                                    userId
                                )
                                .Where(DurableExecutionQueries.Active)
                                .AnyAsync(token)
                                .ConfigureAwait(false)
                        )
                        {
                            throw new AgwException(
                                ErrorCodes.DurableExecutionConflict,
                                "Stop the active Agentflow execution before resuming a checkpoint."
                            );
                        }

                        await RegisterResumeExecutionAsync(
                                session.Agents,
                                record,
                                resumeExecutionId.Value,
                                userId,
                                token
                            )
                            .ConfigureAwait(false);
                    }

                    await session
                        .DeleteConversationHistoryAfterAsync(
                            record.ProjectConversationId,
                            record.BoundarySequence,
                            token
                        )
                        .ConfigureAwait(false);
                    await session
                        .Agents.AgentflowCheckpoints.Where(item =>
                            item.ProjectConversationId == record.ProjectConversationId
                            && item.BoundarySequence > record.BoundarySequence
                        )
                        .ExecuteDeleteAsync(token)
                        .ConfigureAwait(false);
                    return new AgentflowCheckpointPersistenceResult<AgentflowCheckpointSnapshot>(
                        snapshot,
                        Commit: true
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static async Task<ValidatedResumeCheckpoint> LoadValidatedResumeCheckpointAsync(
        IAgentsDbContext dbContext,
        Guid occurrenceId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        bool expectsDurable,
        CancellationToken cancellationToken
    )
    {
        var record =
            await dbContext
                .AgentflowCheckpoints.AsNoTracking()
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
        if (record.IsDurable != expectsDurable)
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

        // Validate decryption and deserialization before any history mutation.
        return new ValidatedResumeCheckpoint(record, ToSnapshot(record));
    }

    private static void EnsureExistingResumeMatches(
        DurableExecutionRecord existingResume,
        Guid checkpointOccurrenceId,
        string userId
    )
    {
        var existingManifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
            existingResume.ManifestJson,
            "durable resume manifest"
        );
        if (existingResume.UserId != userId || existingManifest.ResumeCheckpointOccurrenceId != checkpointOccurrenceId)
        {
            throw new AgwException(ErrorCodes.DurableExecutionConflict);
        }
    }

    private sealed record ValidatedResumeCheckpoint(
        AgentflowCheckpointRecord Record,
        AgentflowCheckpointSnapshot Snapshot
    );

    private async Task RegisterResumeExecutionAsync(
        IAgentsDbContext dbContext,
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
            .DurableExecutions.AsNoTracking()
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
                .DurableExecutions.AsNoTracking()
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
        dbContext.DurableExecutions.Add(
            new DurableExecutionRecord
            {
                Id = resumeExecutionId,
                UserId = userId,
                ProjectId = checkpointRecord.ProjectId,
                ProjectConversationId = checkpointRecord.ProjectConversationId,
                ScopeBackfilled = true,
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
