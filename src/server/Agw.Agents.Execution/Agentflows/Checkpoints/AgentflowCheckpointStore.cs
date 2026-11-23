using System.Security.Cryptography;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Extensions.Contracts;
using Agw.Projects.Contracts.History;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agentflows.Checkpoints;

/// <summary>
/// 持久化 Agentflow checkpoint occurrence，并以聊天序号作为唯一回滚边界。
/// </summary>
public sealed class AgentflowCheckpointStore
{
    private const string CheckpointMessageType = AgwMessageTypes.AgentflowCheckpoint;
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IApplicationLock _applicationLock;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentflowCheckpointStore> _logger;

    public AgentflowCheckpointStore(
        IServiceScopeFactory scopeFactory,
        IApplicationLock applicationLock,
        TimeProvider timeProvider,
        ILogger<AgentflowCheckpointStore> logger
    )
    {
        _scopeFactory = scopeFactory;
        _applicationLock = applicationLock;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal async Task<RecordedAgentflowCheckpoint?> RecordAsync(
        Guid? sourceTurnId,
        Guid projectId,
        Guid conversationId,
        string contextId,
        Guid taskId,
        Guid agentflowId,
        int agentflowRevision,
        AgentflowRevisionSnapshot revisionSnapshot,
        string userId,
        bool isDurable,
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
        if (revisionSnapshot.RootRevision != agentflowRevision)
        {
            throw new AgwException(ErrorCodes.DurableExecutionConflict);
        }

        var occurrenceId = CreateDeterministicGuid(
            sourceTurnId ?? taskId,
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

        // Flush before acquiring the history lock: a flush uses the same Project -> History lock order.
        await using var historyBarrier = ExecutionScope.Current?.History is { } history
            ? await history.EnterBarrierAsync(cancellationToken).ConfigureAwait(false)
            : null;
        await using var lifecycleLock = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(projectId), cancellationToken)
            .ConfigureAwait(false);
        await using var historyLock = await _applicationLock
            .AcquireAsync(GetHistoryLockName(projectId, contextId), cancellationToken)
            .ConfigureAwait(false);
        var turnId = ExecutionScope.Current?.Context.TurnId;
        // Durable 执行的存档行经写入入口在租约检查事务中提交。
        // A durable execution commits its checkpoint rows through the write guard inside a lease-checked transaction.
        if (ExecutionScope.Current?.WriteGuard is { } writeGuard)
        {
            return await writeGuard
                .RunAsync(
                    (services, token) =>
                        RecordCoreAsync(services.GetRequiredService<IAgentflowCheckpointPersistence>(), token),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await RecordCoreAsync(
                scope.ServiceProvider.GetRequiredService<IAgentflowCheckpointPersistence>(),
                cancellationToken
            )
            .ConfigureAwait(false);

        async Task<RecordedAgentflowCheckpoint?> RecordCoreAsync(
            IAgentflowCheckpointPersistence persistence,
            CancellationToken token
        )
        {
            var existing = await persistence.FindCheckpointAsync(occurrenceId, token).ConfigureAwait(false);
            if (existing != null)
            {
                if (!string.Equals(existing.UserId, userId, StringComparison.Ordinal))
                {
                    throw new AgwException(ErrorCodes.InvalidParam, "Agentflow checkpoint owner does not match.");
                }

                if (
                    existing.AgentflowRevision != agentflowRevision
                    || existing.RuntimeVersion != AgentflowCheckpointRuntime.Version
                )
                {
                    throw new AgwException(ErrorCodes.DurableExecutionConflict);
                }

                return new RecordedAgentflowCheckpoint(ToSnapshot(existing, revisionSnapshot), messages);
            }

            if (
                !await persistence
                    .ProjectConversationExistsAsync(projectId, conversationId, contextId, userId, token)
                    .ConfigureAwait(false)
            )
            {
                return null;
            }

            return await persistence
                .ExecuteAsync(
                    async (session, sessionToken) =>
                    {
                        var nextSequence = await session
                            .GetLastConversationSequenceAsync(conversationId, sessionToken)
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
                                {
                                    TurnId = turnId,
                                }
                            );
                        }

                        var record = new AgentflowCheckpointRecord
                        {
                            Id = occurrenceId,
                            SourceTurnId = sourceTurnId,
                            ProjectId = projectId,
                            ProjectConversationId = conversationId,
                            ContextId = contextId,
                            TaskId = taskId,
                            AgentflowId = agentflowId,
                            AgentflowRevision = agentflowRevision,
                            RuntimeVersion = AgentflowCheckpointRuntime.Version,
                            UserId = userId,
                            IsDurable = isDurable,
                            BoundarySequence = nextSequence,
                            MarkersJson = JsonSerializer.Serialize(markers, JsonOptions),
                            CheckpointJson = DurableExecutionJson.Serialize(checkpoint),
                            CreateBy = userId,
                            CreateTime = now,
                            UpdateBy = userId,
                            UpdateTime = now,
                        };
                        session.Agents.AgentflowCheckpoints.Add(record);
                        return new AgentflowCheckpointPersistenceResult<RecordedAgentflowCheckpoint?>(
                            new RecordedAgentflowCheckpoint(ToSnapshot(record, revisionSnapshot), messages),
                            Commit: true
                        );
                    },
                    token,
                    conversationId,
                    ExecutionContextSlot.FindBound(projectId, contextId)?.Generation ?? 0
                )
                .ConfigureAwait(false);
        }
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
        var turns = scope.ServiceProvider.GetRequiredService<IConversationTurnStore>();
        var snapshots = scope.ServiceProvider.GetRequiredService<IAgentflowRevisionSnapshotReader>();

        var records = await ListCheckpointRecordsAsync(
                dbContext,
                projectId,
                contextId,
                agentflowId,
                userId,
                cancellationToken
            )
            .ConfigureAwait(false);
        var result = new List<AgentflowCheckpointAvailability>(records.Length);
        var validationContext = new CheckpointValidationContext
        {
            Agents = dbContext,
            Turns = turns,
            Snapshots = snapshots,
            ProjectId = projectId,
            ContextId = contextId,
            AgentflowId = agentflowId,
            UserId = userId,
        };
        foreach (var record in records)
        {
            var isInProcessOccurrence = inProcessOccurrences?.Contains(record.Id) == true;
            var available = await IsCheckpointAvailableAsync(
                    validationContext,
                    record,
                    isInProcessOccurrence,
                    cancellationToken
                )
                .ConfigureAwait(false);
            result.Add(CreateAvailability(record, available));
        }

        return result;
    }

    private static Task<AgentflowCheckpointRecord[]> ListCheckpointRecordsAsync(
        IAgentsDbContext dbContext,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        CancellationToken cancellationToken
    ) =>
        dbContext
            .AgentflowCheckpoints.AsNoTracking()
            .Where(item =>
                item.ProjectId == projectId
                && item.ContextId == contextId
                && item.AgentflowId == agentflowId
                && item.UserId == userId
            )
            .OrderBy(item => item.BoundarySequence)
            .ToArrayAsync(cancellationToken);

    private async Task<bool> IsCheckpointAvailableAsync(
        CheckpointValidationContext validationContext,
        AgentflowCheckpointRecord record,
        bool isInProcessOccurrence,
        CancellationToken cancellationToken
    )
    {
        if (
            record.RuntimeVersion != AgentflowCheckpointRuntime.Version
            || !HasReadableCheckpoint(record)
            || (!record.IsDurable && !isInProcessOccurrence)
        )
        {
            return false;
        }

        try
        {
            _ = await LoadValidatedResumeCheckpointAsync(
                    validationContext,
                    record.Id,
                    record.IsDurable,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return true;
        }
        catch (AgwException exception)
        {
            _logger.LogWarning(
                exception,
                "Agentflow checkpoint {CheckpointOccurrenceId} is unavailable with error code {ErrorCode}.",
                record.Id,
                exception.Code
            );
            return false;
        }
    }

    private static AgentflowCheckpointAvailability CreateAvailability(
        AgentflowCheckpointRecord record,
        bool available
    ) =>
        new(
            record.Id,
            record.AgentflowId,
            record.BoundarySequence,
            available,
            DeserializeMarkers(record.MarkersJson)
                .Select(marker => new AgentflowCheckpointMarkerInfo(marker.NodeId, marker.Name, marker.MessageId))
                .ToArray()
        );

    internal async Task<AgentflowCheckpointSnapshot> PrepareInProcessResumeAsync(
        Guid occurrenceId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        CancellationToken cancellationToken,
        Func<AgentflowRevisionSnapshot, CancellationToken, Task>? validateSnapshot = null
    ) =>
        await PrepareResumeAsync(
                occurrenceId,
                projectId,
                contextId,
                agentflowId,
                userId,
                resumeTurnId: null,
                cancellationToken,
                validateSnapshot: validateSnapshot
            )
            .ConfigureAwait(false);

    internal async Task<AgentflowCheckpointSnapshot> PrepareDistributedResumeAsync(
        Guid occurrenceId,
        Guid resumeTurnId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        CancellationToken cancellationToken,
        DurableExecutionSettings? permissionSettings = null,
        Func<AgentflowRevisionSnapshot, CancellationToken, Task>? validateSnapshot = null
    ) =>
        await PrepareResumeAsync(
                occurrenceId,
                projectId,
                contextId,
                agentflowId,
                userId,
                resumeTurnId,
                cancellationToken,
                permissionSettings,
                validateSnapshot
            )
            .ConfigureAwait(false);

    internal async Task<Guid?> GetSourceTurnIdAsync(
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
            .Select(item => item.SourceTurnId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AgentflowCheckpointSnapshot> PrepareResumeAsync(
        Guid occurrenceId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        Guid? resumeTurnId,
        CancellationToken cancellationToken,
        DurableExecutionSettings? permissionSettings = null,
        Func<AgentflowRevisionSnapshot, CancellationToken, Task>? validateSnapshot = null
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IAgentflowCheckpointPersistence>();
        var turns = scope.ServiceProvider.GetRequiredService<IConversationTurnStore>();
        var snapshots = scope.ServiceProvider.GetRequiredService<IAgentflowRevisionSnapshotReader>();
        var checkpoint =
            await persistence.FindCheckpointAsync(occurrenceId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.InvalidParam, "Agentflow checkpoint was not found.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100), _timeProvider);
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        IApplicationLockLease executionLease;
        try
        {
            executionLease = await _applicationLock.AcquireAsync(
                ConversationExecutionLock.GetResourceName(checkpoint.ProjectConversationId),
                acquisition.Token
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }
        await using var heldExecutionLease = executionLease;
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            executionLease.HandleLostToken
        );
        cancellationToken = mutation.Token;
        await using var lifecycleLock = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(projectId), cancellationToken)
            .ConfigureAwait(false);
        await using var historyLock = await _applicationLock
            .AcquireAsync(GetHistoryLockName(projectId, contextId), cancellationToken)
            .ConfigureAwait(false);
        var dbContext = scope.ServiceProvider.GetRequiredService<IAgentsDbContext>();
        var validationContext = new CheckpointValidationContext
        {
            Agents = dbContext,
            Turns = turns,
            Snapshots = snapshots,
            ProjectId = projectId,
            ContextId = contextId,
            AgentflowId = agentflowId,
            UserId = userId,
        };
        var validatedTarget = await LoadValidatedResumeCheckpointAsync(
                validationContext,
                occurrenceId,
                expectsDurable: resumeTurnId.HasValue,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (validateSnapshot is not null)
        {
            await validateSnapshot(validatedTarget.Snapshot.RevisionSnapshot, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<Guid> extensionIds = [];
        if (resumeTurnId.HasValue)
        {
            var existingResume = await dbContext
                .DurableExecutions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == resumeTurnId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (existingResume != null)
            {
                EnsureExistingResumeMatches(existingResume, validatedTarget.Record.Id, userId);
                return validatedTarget.Snapshot;
            }

            var target = validatedTarget;
            var source =
                await dbContext
                    .DurableExecutions.AsNoTracking()
                    .SingleOrDefaultAsync(
                        item => item.Id == target.Record.SourceTurnId && item.UserId == userId,
                        cancellationToken
                    )
                    .ConfigureAwait(false)
                ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
            extensionIds = ReadExecutionManifest(source).ExtensionIds;
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

        var extensionLeases = new List<IExtensionGenerationLease>(extensionIds.Count);
        try
        {
            if (resumeTurnId.HasValue)
            {
                var generations = scope.ServiceProvider.GetRequiredService<IExtensionGenerationCatalog>();
                foreach (var extensionId in extensionIds.Order())
                {
                    extensionLeases.Add(
                        await generations.AcquireCurrentAsync(extensionId, cancellationToken).ConfigureAwait(false)
                    );
                }
            }

            return await persistence
                .ExecuteAsync(
                    async (session, token) =>
                    {
                        var validated = await LoadValidatedResumeCheckpointAsync(
                                validationContext with
                                {
                                    Agents = session.Agents,
                                },
                                occurrenceId,
                                resumeTurnId.HasValue,
                                token
                            )
                            .ConfigureAwait(false);
                        var record = validated.Record;
                        var snapshot = validated.Snapshot;

                        if (resumeTurnId.HasValue)
                        {
                            var existingResume = await session
                                .Agents.DurableExecutions.AsNoTracking()
                                .SingleOrDefaultAsync(item => item.Id == resumeTurnId.Value, token)
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

                            if (extensionIds.Count > 0)
                            {
                                await scope
                                    .ServiceProvider.GetRequiredService<IExtensionAdmissionValidator>()
                                    .ValidateEnabledAsync(extensionIds, token)
                                    .ConfigureAwait(false);
                            }
                            await RegisterResumeExecutionAsync(
                                    session.Agents,
                                    record,
                                    resumeTurnId.Value,
                                    userId,
                                    token,
                                    permissionSettings
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
                        if (resumeTurnId.HasValue)
                        {
                            // 恢复分支是一个新的 Turn：Turn 行与序号 1 的开始事件随执行记录一起提交。
                            // The resume branch is a new turn: its turn row and the start event with sequence 1 commit with the execution record.
                            await session
                                .AcceptResumeTurnAsync(resumeTurnId.Value, record, snapshot.RevisionSnapshot, token)
                                .ConfigureAwait(false);
                            AddResumeStartEvent(session.Agents, resumeTurnId.Value, record, userId);
                        }
                        return new AgentflowCheckpointPersistenceResult<AgentflowCheckpointSnapshot>(
                            snapshot,
                            Commit: true
                        );
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        finally
        {
            await ExtensionCapabilityCatalog.DisposeLeasesAsync(extensionLeases).ConfigureAwait(false);
        }
    }

    private async Task<ValidatedResumeCheckpoint> LoadValidatedResumeCheckpointAsync(
        CheckpointValidationContext context,
        Guid occurrenceId,
        bool expectsDurable,
        CancellationToken cancellationToken
    )
    {
        var record =
            await context
                .Agents.AgentflowCheckpoints.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == occurrenceId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.InvalidParam, "Agentflow checkpoint was not found.");
        EnsureCheckpointTargetMatches(record, context, expectsDurable);
        var sourceTurn = await GetCheckpointSourceTurnAsync(context.Turns, record, cancellationToken)
            .ConfigureAwait(false);
        var revisionSnapshot = AgentflowRevisionSnapshot.FromTurn(sourceTurn);
        EnsureCheckpointRevisionMatches(record, sourceTurn, context.AgentflowId, revisionSnapshot);

        if (record.IsDurable)
        {
            await EnsureDurableSnapshotMatchesAsync(context, record, sourceTurn, revisionSnapshot, cancellationToken)
                .ConfigureAwait(false);
        }

        if (
            !await context
                .Snapshots.DefinitionsRemainAvailableAsync(sourceTurn.TargetId, revisionSnapshot, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "A definition pinned by this checkpoint is no longer available."
            );
        }

        // Validate decryption and deserialization before any history mutation.
        return new ValidatedResumeCheckpoint(record, ToSnapshot(record, revisionSnapshot));
    }

    private static void EnsureCheckpointTargetMatches(
        AgentflowCheckpointRecord record,
        CheckpointValidationContext context,
        bool expectsDurable
    )
    {
        if (
            record.ProjectId != context.ProjectId
            || !string.Equals(record.ContextId, context.ContextId, StringComparison.Ordinal)
            || record.AgentflowId != context.AgentflowId
            || !string.Equals(record.UserId, context.UserId, StringComparison.Ordinal)
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

        if (record.RuntimeVersion != AgentflowCheckpointRuntime.Version)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The checkpoint runtime version is no longer available.");
        }
    }

    private static async Task<ConversationTurnSnapshot> GetCheckpointSourceTurnAsync(
        IConversationTurnStore turns,
        AgentflowCheckpointRecord record,
        CancellationToken cancellationToken
    )
    {
        var sourceTurn = record.SourceTurnId.HasValue
            ? await turns.GetAsync(record.SourceTurnId.Value, cancellationToken).ConfigureAwait(false)
            : null;
        if (
            sourceTurn is null
            || sourceTurn.TargetType != ConversationTurnTargetType.Agentflow
            || sourceTurn.AgentflowDependencies is null
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The checkpoint's source turn has no Agentflow revision snapshot."
            );
        }

        return sourceTurn;
    }

    private static void EnsureCheckpointRevisionMatches(
        AgentflowCheckpointRecord record,
        ConversationTurnSnapshot sourceTurn,
        Guid agentflowId,
        AgentflowRevisionSnapshot revisionSnapshot
    )
    {
        if (
            sourceTurn.TargetId != agentflowId
            || record.AgentflowId != sourceTurn.TargetId
            || !revisionSnapshot.PinsDefinition(sourceTurn.TargetId, record.AgentflowId, record.AgentflowRevision)
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "A definition pinned by this checkpoint is no longer available."
            );
        }
    }

    private static async Task EnsureDurableSnapshotMatchesAsync(
        CheckpointValidationContext context,
        AgentflowCheckpointRecord record,
        ConversationTurnSnapshot sourceTurn,
        AgentflowRevisionSnapshot revisionSnapshot,
        CancellationToken cancellationToken
    )
    {
        var sourceExecution = await context
            .Agents.DurableExecutions.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == record.SourceTurnId && item.UserId == context.UserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (sourceExecution is null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The checkpoint's revision snapshots do not match.");
        }

        var manifest = ReadExecutionManifest(sourceExecution);
        if (
            manifest.AgentType != AgentRuntimeType.Agentflow
            || manifest.AgentId != sourceTurn.TargetId
            || manifest.AgentflowRevision != revisionSnapshot.RootRevision
            || !revisionSnapshot.MatchesDependencies(manifest.AgentflowDependencies)
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The checkpoint's revision snapshots do not match.");
        }
    }

    private static DurableExecutionManifest ReadExecutionManifest(DurableExecutionRecord record)
    {
        var scope = DurableExecutionManifestScopeReader.Read(record.ManifestJson, record.Id, record.UserId);
        if (
            !record.ScopeBackfilled
            || scope == null
            || record.ProjectId != scope.ProjectId
            || record.ProjectConversationId != scope.ProjectConversationId
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "The stored execution manifest has incomplete or inconsistent scope."
            );
        }
        return DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
            record.ManifestJson,
            "durable execution manifest"
        );
    }

    private static void EnsureExistingResumeMatches(
        DurableExecutionRecord existingResume,
        Guid checkpointOccurrenceId,
        string userId
    )
    {
        var existingManifest = ReadExecutionManifest(existingResume);
        if (existingResume.UserId != userId || existingManifest.ResumeCheckpointOccurrenceId != checkpointOccurrenceId)
        {
            throw new AgwException(ErrorCodes.DurableExecutionConflict);
        }
    }

    private sealed record ValidatedResumeCheckpoint(
        AgentflowCheckpointRecord Record,
        AgentflowCheckpointSnapshot Snapshot
    );

    private sealed record CheckpointValidationContext
    {
        public required IAgentsDbContext Agents { get; init; }
        public required IConversationTurnStore Turns { get; init; }
        public required IAgentflowRevisionSnapshotReader Snapshots { get; init; }
        public required Guid ProjectId { get; init; }
        public required string ContextId { get; init; }
        public required Guid AgentflowId { get; init; }
        public required string UserId { get; init; }
    }

    private async Task RegisterResumeExecutionAsync(
        IAgentsDbContext dbContext,
        AgentflowCheckpointRecord checkpointRecord,
        Guid resumeTurnId,
        string userId,
        CancellationToken cancellationToken,
        DurableExecutionSettings? permissionSettings = null
    )
    {
        if (!checkpointRecord.IsDurable || !checkpointRecord.SourceTurnId.HasValue)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The selected checkpoint is not a durable checkpoint.");
        }
        if (resumeTurnId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "resumeTurnId is required.");
        }

        var existing = await dbContext
            .DurableExecutions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == resumeTurnId, cancellationToken)
            .ConfigureAwait(false);
        if (existing != null)
        {
            var existingManifest = ReadExecutionManifest(existing);
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
                    item => item.Id == checkpointRecord.SourceTurnId.Value && item.UserId == userId,
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

        var manifest = ReadExecutionManifest(source) with
        {
            TurnId = resumeTurnId,
            StreamingScopeId = resumeTurnId.ToString("D"),
            ResumeCheckpointOccurrenceId = checkpointRecord.Id,
            ResumeCheckpointNodeIds = DeserializeMarkers(checkpointRecord.MarkersJson)
                .Select(item => item.NodeId)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };
        var selected = permissionSettings ?? manifest.Settings;
        manifest = manifest with
        {
            ExtensionIds = manifest.ExtensionIds,
            Settings = manifest.Settings with
            {
                PermissionMode = selected.NextPermissionMode ?? selected.PermissionMode,
                PermissionVersion = Math.Max(selected.PermissionVersion, selected.NextPermissionVersion),
                NextPermissionMode = null,
                NextPermissionVersion = 0,
            },
        };
        var now = _timeProvider.GetUtcNow();
        dbContext.DurableExecutions.Add(
            new DurableExecutionRecord
            {
                Id = resumeTurnId,
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
                LastEventSequence = 1,
                CreateBy = userId,
                CreateTime = now,
                UpdateBy = userId,
                UpdateTime = now,
            }
        );
    }

    /// <summary>
    /// 恢复分支的开始事件，序号为 1；Worker 领取后继续分配之后的序号。
    /// The start event of a resume branch with sequence 1; the worker continues with later sequences after claiming it.
    /// </summary>
    private static void AddResumeStartEvent(
        IAgentsDbContext dbContext,
        Guid resumeTurnId,
        AgentflowCheckpointRecord checkpointRecord,
        string userId
    )
    {
        var start = TurnMessageFactory.CreateStarted(
            new TurnEnvelope(
                resumeTurnId,
                checkpointRecord.ProjectConversationId,
                checkpointRecord.AgentflowId,
                AgentRuntimeType.Agentflow,
                resumeTurnId.ToString("D")
            )
        );
        dbContext.DurableExecutionEvents.Add(
            new DurableExecutionEventRecord
            {
                Id = Guid.CreateVersion7(),
                TurnId = resumeTurnId,
                TurnSequence = 1,
                LeaseEpoch = 0,
                SegmentIndex = 0,
                PayloadJson = JsonUtil.Serialize(start),
                CreateBy = userId,
                UpdateBy = userId,
            }
        );
    }

    private static AgentflowCheckpointSnapshot ToSnapshot(
        AgentflowCheckpointRecord record,
        AgentflowRevisionSnapshot revisionSnapshot
    ) =>
        new(
            record.Id,
            record.SourceTurnId,
            record.AgentflowId,
            record.BoundarySequence,
            record.AgentflowRevision,
            record.RuntimeVersion,
            DeserializeMarkers(record.MarkersJson),
            revisionSnapshot,
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
            _ = DeserializeMarkers(record.MarkersJson);
            _ = DurableExecutionJson.DeserializeRequired<DurableAgentflowCheckpoint>(
                record.CheckpointJson,
                "agentflow checkpoint"
            );
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
            [AgwMessageClassifier.TypeKey] = CheckpointMessageType,
            ["checkpointOccurrenceId"] = occurrenceId.ToString("D"),
            [AgwMessageClassifier.NodeIdKey] = marker.NodeId,
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
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        Sha256Util.HashUtf8(input, hash);
        return new Guid(hash[..16]);
    }

    private static string GetHistoryLockName(Guid projectId, string contextId) =>
        ConversationHistoryLock.GetResourceName(projectId, contextId);
}
