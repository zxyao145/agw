using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Execution.Persistence.Durable;

/// <summary>
/// 从 durable_execution 单行状态机还原的 execution 快照。
/// The execution snapshot restored from the single-row durable_execution state machine.
/// </summary>
internal sealed record DurableExecutionSnapshot
{
    public required DurableExecutionManifest Manifest { get; init; }

    public required DurableExecutionStatus Status { get; init; }

    /// <summary>
    /// 下一次需要执行的分段序号。
    /// The next segment index to run.
    /// </summary>
    public required int SegmentIndex { get; init; }

    public Guid StateVersion { get; init; }

    public DurableAgentflowCheckpoint? Checkpoint { get; init; }

    public IReadOnlyList<InteractionRequest> PendingInteractions { get; init; } = [];

    public IReadOnlyList<InteractionResponse> Responses { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<UserInputInteraction> InputCatalog { get; init; } = [];

    public IReadOnlyList<DurableResolvedInteraction> ResolvedInputs { get; init; } = [];
}

internal static class DurableExecutionSnapshotExtensions
{
    /// <summary>
    /// 返回当前仍未收到回答的人工请求。
    /// Returns the human requests still waiting for an answer.
    /// </summary>
    public static IReadOnlyList<InteractionRequest> GetUnansweredInteractions(this DurableExecutionSnapshot snapshot)
    {
        var answered = snapshot.Responses.Select(item => item.InteractionId).ToHashSet(StringComparer.Ordinal);
        return snapshot.PendingInteractions.Where(item => !answered.Contains(item.InteractionId)).ToArray();
    }

    /// <summary>
    /// 从持久化 checkpoint、pending 和 response 构造下一分段输入。
    /// Builds the next segment input from the persisted checkpoint, pending requests and responses.
    /// </summary>
    public static DurableExecutionSegmentInput CreateSegmentInput(this DurableExecutionSnapshot snapshot)
    {
        var responses = snapshot.Responses.ToDictionary(item => item.InteractionId, StringComparer.Ordinal);
        if (
            responses.Count != snapshot.Responses.Count
            || responses.Count != snapshot.PendingInteractions.Count
            || snapshot.PendingInteractions.Any(item => !responses.ContainsKey(item.InteractionId))
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "A resumable execution requires one response for every pending interaction."
            );
        }

        var resolved = snapshot
            .PendingInteractions.Select(item => new DurableResolvedInteraction(item, responses[item.InteractionId]))
            .ToArray();
        return new DurableExecutionSegmentInput(
            snapshot.Manifest.ExecutionId,
            snapshot.SegmentIndex,
            resolved,
            snapshot.Checkpoint
        )
        {
            InputCatalog = snapshot.InputCatalog,
            ResolvedInputs = snapshot
                .ResolvedInputs.Concat(resolved.Where(item => item.Request is UserInputInteraction))
                .DistinctBy(item => item.Request.InteractionId)
                .ToArray(),
        };
    }
}

/// <summary>
/// durable_execution 单行状态机的读写：启动清单、状态、checkpoint、pending 与 response。执行结果在租约检查事务中写入，
/// 用户回答、权限切换与中断属于用户命令，按 StateVersion 做乐观并发。
/// Reads and writes the single-row durable_execution state machine: manifest, status, checkpoint, pending requests and responses. Execution results are written inside lease-checked transactions,
/// while answers, permission changes and interrupts are user commands using StateVersion optimistic concurrency.
/// </summary>
internal sealed class DurableExecutionStore
{
    private readonly IAgentsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly IApplicationLock _applicationLock;
    private readonly IDurableExecutionScopeMaintenance _scopeMaintenance;

    public DurableExecutionStore(
        IAgentsDbContext dbContext,
        TimeProvider timeProvider,
        IApplicationLock applicationLock,
        IDurableExecutionScopeMaintenance scopeMaintenance
    )
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _applicationLock = applicationLock;
        _scopeMaintenance = scopeMaintenance;
    }

    /// <summary>
    /// 由已受理的请求生成加密前的启动清单 JSON。
    /// Builds the manifest JSON, before encryption, from an accepted request.
    /// </summary>
    internal static string CreateManifestJson(ExecutionRequest request) =>
        DurableExecutionJson.Serialize(
            new DurableExecutionManifest
            {
                ExecutionId = request.TurnId,
                UserId = request.UserId,
                AgentId = request.Target.AgentId,
                AgentType = request.Target.AgentType,
                Input = request.Input,
                Task = DurableExecutionMapper.FromProjection(request.Task),
                Settings = DurableExecutionMapper.FromSettings(request.Settings),
                WorkspaceSnapshot = request.WorkspaceSnapshot,
                StreamingScopeId = request.Envelope.StreamingScopeId,
            }
        );

    internal async Task<DurableExecutionSnapshot> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var record =
            await FindAsync(executionId, userId: null, tracking: false, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        return ToSnapshot(record);
    }

    /// <summary>
    /// 读取刚领取的记录；清单或所属范围无效的记录被隔离并返回空。
    /// Reads a just-claimed record; a record with an invalid manifest or scope is quarantined and null is returned.
    /// </summary>
    internal async Task<DurableExecutionSnapshot?> LoadClaimedAsync(
        Guid executionId,
        CancellationToken cancellationToken
    )
    {
        var record = await _scopeMaintenance
            .LoadValidatedExecutionAsync(executionId, cancellationToken)
            .ConfigureAwait(false);
        return record == null ? null : ToSnapshot(record);
    }

    /// <summary>
    /// 按 executionId 和 owner 同时加载快照，避免向其他用户泄露执行是否存在。
    /// Loads the snapshot by executionId and owner together so the existence of another user's execution is not disclosed.
    /// </summary>
    internal async Task<DurableExecutionSnapshot> GetAuthorizedAsync(
        Guid executionId,
        string userId,
        CancellationToken cancellationToken
    )
    {
        var record =
            await FindAsync(executionId, userId, tracking: false, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        var snapshot = ToSnapshot(record);
        await EnsureSessionCurrentAsync(snapshot, cancellationToken);
        return snapshot;
    }

    /// <summary>
    /// Loads the authorized execution status without materializing the encrypted execution manifest.
    /// The encrypted error is loaded only for failed executions.
    /// </summary>
    internal async Task<DurableExecutionOutcome> GetAuthorizedOutcomeAsync(
        Guid executionId,
        string userId,
        CancellationToken cancellationToken
    )
    {
        var state = await _dbContext
            .DurableExecutions.AsNoTracking()
            .Where(item => item.Id == executionId && item.UserId == userId)
            .Select(item => new { item.Id, item.Status })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (state == null)
        {
            throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        }

        string? errorMessage = null;
        if (state.Status == DurableExecutionStatus.Failed)
        {
            var record = await FindAsync(executionId, userId, tracking: false, cancellationToken).ConfigureAwait(false);
            errorMessage = record?.ErrorMessage;
        }

        return new DurableExecutionOutcome(state.Id, state.Status, errorMessage);
    }

    /// <summary>
    /// 在调用方的租约检查事务中持久化一个分段的 checkpoint、pending 或终态。
    /// Persists a segment's checkpoint, pending requests or terminal state inside the caller's lease-checked transaction.
    /// </summary>
    internal async Task<DurableExecutionSnapshot> ApplySegmentResultAsync(
        DurableExecutionSegmentResult result,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        var record =
            await FindAsync(result.ExecutionId, userId: null, tracking: true, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        if (record.Status != DurableExecutionStatus.Running || record.SegmentIndex != result.SegmentIndex)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "The persisted execution state does not match the completed segment."
            );
        }

        ApplySegmentResult(record, result);
        await SaveStateAsync(record, cancellationToken).ConfigureAwait(false);
        return ToSnapshot(record);
    }

    /// <summary>
    /// 受理后、执行开始前的失败：在调用方的事务中把 Queued 或 Running 的记录写为 Failed；记录已经被其他命令结束时返回假。
    /// A failure after acceptance and before execution: writes a Queued or Running record as Failed inside the caller's transaction; returns false when another command already ended it.
    /// </summary>
    internal async Task<bool> FailAcceptedAsync(
        Guid executionId,
        string errorMessage,
        CancellationToken cancellationToken
    )
    {
        var record =
            await FindAsync(executionId, userId: null, tracking: true, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        if (record.Status is not (DurableExecutionStatus.Queued or DurableExecutionStatus.Running))
            return false;
        SetTerminal(record, DurableExecutionStatus.Failed, errorMessage);
        await SaveStateAsync(record, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal async Task<DurableExecutionSnapshot> SetPermissionModeAsync(
        Guid executionId,
        string userId,
        AgwPermissionMode mode,
        CancellationToken cancellationToken
    )
    {
        if (!Enum.IsDefined(mode))
            throw new AgwException(ErrorCodes.InvalidParam, "Unknown permission mode.");
        await using var permissionLock = await _applicationLock
            .AcquireAsync($"agw:execution:permissions:{executionId:N}", cancellationToken)
            .ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            ClearTrackedDurableExecutions();
            var record =
                await FindAsync(executionId, userId, tracking: true, cancellationToken).ConfigureAwait(false)
                ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
            var snapshot = ToSnapshot(record);
            await EnsureSessionCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (
                (snapshot.Manifest.Settings.NextPermissionMode ?? snapshot.Manifest.Settings.PermissionMode) == mode
                || record.Status
                    is DurableExecutionStatus.Completed
                        or DurableExecutionStatus.Failed
                        or DurableExecutionStatus.Interrupted
            )
                return snapshot;
            record.ManifestJson = DurableExecutionJson.Serialize(
                snapshot.Manifest with
                {
                    Settings = snapshot.Manifest.Settings with
                    {
                        NextPermissionMode = mode,
                        NextPermissionVersion = checked(
                            Math.Max(
                                snapshot.Manifest.Settings.NextPermissionVersion,
                                snapshot.Manifest.Settings.PermissionVersion
                            ) + 1
                        ),
                    },
                }
            );
            try
            {
                await SaveStateAsync(
                        record,
                        cancellationToken,
                        preserveExecutionVersion: snapshot.Status == DurableExecutionStatus.Running
                    )
                    .ConfigureAwait(false);
                return ToSnapshot(record);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2) { }
            catch (DbUpdateConcurrencyException)
            {
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "The permission mode changed concurrently. Retry the command."
                );
            }
        }
    }

    /// <summary>
    /// 校验 pending request 后持久化人工回答；全部回答到齐时把状态推进到 Resuming。
    /// Persists a human answer after validating its pending request; once every answer arrives the status advances to Resuming.
    /// </summary>
    internal async Task<DurableExecutionSnapshot> SubmitHumanResponseAsync(
        SubmitDurableHumanResponseRequest request,
        string userId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var permissionLock = await _applicationLock
            .AcquireAsync($"agw:execution:permissions:{request.ExecutionId:N}", cancellationToken)
            .ConfigureAwait(false);
        var requestId = request.Response.InteractionId;
        ClearTrackedDurableExecutions();
        var record =
            await FindAsync(request.ExecutionId, userId, tracking: true, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        var snapshot = ToSnapshot(record);
        await EnsureSessionCurrentAsync(snapshot, cancellationToken);
        var pending =
            snapshot.PendingInteractions.SingleOrDefault(item => item.InteractionId == requestId)
            ?? throw new AgwException(ErrorCodes.HumanInteractionNotFound);
        var response = InteractionRules.ValidateAndNormalize(
            pending,
            request.Response,
            snapshot.Manifest.Settings.PermissionMode
        );
        var existing = snapshot.Responses.SingleOrDefault(item =>
            string.Equals(item.InteractionId, requestId, StringComparison.Ordinal)
        );
        if (existing != null)
        {
            if (
                string.Equals(
                    DurableExecutionJson.Serialize(existing),
                    DurableExecutionJson.Serialize(response),
                    StringComparison.Ordinal
                )
            )
            {
                return snapshot;
            }

            throw new AgwException(ErrorCodes.DurableExecutionConflict);
        }
        if (
            snapshot.Status != DurableExecutionStatus.WaitingForHuman
            || !snapshot.PendingInteractions.Any(item =>
                string.Equals(item.InteractionId, requestId, StringComparison.Ordinal)
            )
        )
        {
            throw new AgwException(ErrorCodes.HumanInteractionNotFound);
        }

        var responses = snapshot.Responses.Append(response).ToArray();
        record.ResponsesJson = DurableExecutionJson.Serialize(responses);
        if (responses.Length == snapshot.PendingInteractions.Count)
        {
            record.Status = DurableExecutionStatus.Resuming;
        }

        try
        {
            await SaveStateAsync(record, cancellationToken).ConfigureAwait(false);
            return ToSnapshot(record);
        }
        catch (DbUpdateConcurrencyException)
        {
            ClearTrackedDurableExecutions();
            var current = await GetAuthorizedAsync(request.ExecutionId, userId, cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == DurableExecutionStatus.Interrupted)
            {
                throw new AgwException(ErrorCodes.HumanInteractionNotFound);
            }

            throw;
        }
    }

    /// <summary>
    /// 在调用方的事务中把未结束的执行写为 Interrupted 并清除租约；返回是否由本次写入结束。条件更新锁定执行行，
    /// 与租约检查事务互斥，正在运行的实例之后的写入都会被拒绝。
    /// Writes an unfinished execution as Interrupted and clears its lease inside the caller's transaction; returns whether this write ended it. The conditional update locks the execution row,
    /// excluding lease-checked transactions, so every later write of a running instance is rejected.
    /// </summary>
    internal async Task<bool> InterruptAsync(Guid executionId, string userId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var updatedCount = await _dbContext
            .DurableExecutions.Where(item => item.Id == executionId && item.UserId == userId)
            .Where(DurableExecutionQueries.Active)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(item => item.Status, DurableExecutionStatus.Interrupted)
                        .SetProperty(item => item.WorkerId, (string?)null)
                        .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.CheckpointJson, (string?)null)
                        .SetProperty(item => item.TurnCheckpointJson, (string?)null)
                        .SetProperty(item => item.PendingInteractionsJson, (string?)null)
                        .SetProperty(item => item.ResponsesJson, (string?)null)
                        .SetProperty(item => item.StateChangedAt, now)
                        .SetProperty(item => item.UpdateBy, userId)
                        .SetProperty(item => item.UpdateTime, now)
                        .SetProperty(item => item.StateVersion, Guid.CreateVersion7()),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (updatedCount > 0)
        {
            return true;
        }

        _ =
            await FindAsync(executionId, userId, tracking: false, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        return false;
    }

    private Task<DurableExecutionRecord?> FindAsync(
        Guid executionId,
        string? userId,
        bool tracking,
        CancellationToken cancellationToken
    )
    {
        IQueryable<DurableExecutionRecord> query = _dbContext.DurableExecutions;
        if (!tracking)
        {
            query = query.AsNoTracking();
        }
        if (userId != null)
        {
            query = query.Where(item => item.UserId == userId);
        }

        return query.SingleOrDefaultAsync(item => item.Id == executionId, cancellationToken);
    }

    /// <summary>
    /// 把分段结果映射到同一条 execution 状态记录；终态同时清除租约。
    /// Maps a segment result onto the execution record; a terminal state also clears the lease.
    /// </summary>
    private static void ApplySegmentResult(DurableExecutionRecord record, DurableExecutionSegmentResult result)
    {
        switch (result.Status)
        {
            case DurableExecutionSegmentStatus.WaitingForHuman:
                ValidatePendingInteractions(result.PendingInteractions);
                record.Status = DurableExecutionStatus.WaitingForHuman;
                record.SegmentIndex = checked(result.SegmentIndex + 1);
                record.WorkerId = null;
                record.LeaseExpiresAt = null;
                record.CheckpointJson =
                    result.Checkpoint == null ? null : DurableExecutionJson.Serialize(result.Checkpoint);
                var previous = ToSnapshot(record);
                var retainedInputs = previous
                    .ResolvedInputs.Concat(
                        previous
                            .PendingInteractions.OfType<UserInputInteraction>()
                            .Select(request => new
                            {
                                Request = request,
                                Response = previous
                                    .Responses.OfType<UserInputResponse>()
                                    .SingleOrDefault(response => response.InteractionId == request.InteractionId),
                            })
                            .Where(item => item.Response is not null)
                            .Select(item => new DurableResolvedInteraction(item.Request, item.Response!))
                    )
                    .DistinctBy(item => item.Request.InteractionId)
                    .ToArray();
                record.PendingInteractionsJson = DurableExecutionJson.Serialize(
                    new DurableInteractionState
                    {
                        Pending = result.PendingInteractions,
                        InputCatalog = result.InputCatalog,
                        ResolvedInputs = retainedInputs,
                    }
                );
                record.ResponsesJson = null;
                record.ErrorMessage = null;
                break;
            case DurableExecutionSegmentStatus.Completed:
                SetTerminal(record, DurableExecutionStatus.Completed, errorMessage: null);
                break;
            case DurableExecutionSegmentStatus.Failed:
                SetTerminal(
                    record,
                    DurableExecutionStatus.Failed,
                    result.ErrorMessage ?? "Distributed execution failed."
                );
                break;
            default:
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    $"Unsupported durable segment status '{result.Status}'."
                );
        }
    }

    private static void ValidatePendingInteractions(IReadOnlyList<InteractionRequest> pending)
    {
        var distinct = pending.Select(item => item.InteractionId).Distinct(StringComparer.Ordinal).Count();
        if (
            pending.Count == 0
            || distinct != pending.Count
            || pending.Any(item => string.IsNullOrWhiteSpace(item.InteractionId) || item.InteractionId.Length > 128)
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "A waiting durable segment requires valid, unique pending interactions."
            );
        }
    }

    /// <summary>
    /// 把记录转换为已解密且经过 schema 校验的 execution 快照。
    /// Converts a record into a decrypted, schema-validated execution snapshot.
    /// </summary>
    private static DurableExecutionSnapshot ToSnapshot(DurableExecutionRecord record)
    {
        var manifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
            record.ManifestJson,
            "durable execution manifest"
        );
        if (manifest.SchemaVersion != DurableExecutionManifest.CurrentSchemaVersion)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Execution '{record.Id}' uses unsupported manifest schema version '{manifest.SchemaVersion}'."
            );
        }
        if (manifest.ExecutionId != record.Id)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Execution '{record.Id}' contains an inconsistent manifest."
            );
        }
        if (
            string.IsNullOrWhiteSpace(manifest.UserId)
            || !string.Equals(manifest.UserId, record.UserId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Execution '{record.Id}' contains an inconsistent owner."
            );
        }

        if (
            !record.ScopeBackfilled
            || manifest.WorkspaceSnapshot == null
            || manifest.Task == null
            || manifest.Input == null
            || manifest.Settings == null
            || manifest.Task.ProjectId == Guid.Empty
            || manifest.Task.ProjectConversationId == Guid.Empty
            || record.ProjectId != manifest.Task.ProjectId
            || record.ProjectConversationId != manifest.Task.ProjectConversationId
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"Execution '{record.Id}' contains an incomplete or inconsistent manifest scope."
            );
        }

        var interactionState = string.IsNullOrWhiteSpace(record.PendingInteractionsJson)
            ? new DurableInteractionState()
            : DurableExecutionJson.DeserializeRequired<DurableInteractionState>(
                record.PendingInteractionsJson,
                "durable interaction state"
            );
        return new DurableExecutionSnapshot
        {
            Manifest = manifest,
            Status = record.Status,
            SegmentIndex = record.SegmentIndex,
            StateVersion = record.StateVersion,
            Checkpoint = string.IsNullOrWhiteSpace(record.CheckpointJson)
                ? null
                : DurableExecutionJson.DeserializeRequired<DurableAgentflowCheckpoint>(
                    record.CheckpointJson,
                    "durable execution checkpoint"
                ),
            PendingInteractions = interactionState.Pending,
            InputCatalog = interactionState.InputCatalog,
            ResolvedInputs = interactionState.ResolvedInputs,
            Responses = string.IsNullOrWhiteSpace(record.ResponsesJson)
                ? []
                : DurableExecutionJson.DeserializeRequired<InteractionResponse[]>(
                    record.ResponsesJson,
                    "durable execution responses"
                ),
            ErrorMessage = record.ErrorMessage,
        };
    }

    private static void SetTerminal(DurableExecutionRecord record, DurableExecutionStatus status, string? errorMessage)
    {
        record.Status = status;
        record.WorkerId = null;
        record.LeaseExpiresAt = null;
        record.CheckpointJson = null;
        record.TurnCheckpointJson = null;
        record.PendingInteractionsJson = null;
        record.ResponsesJson = null;
        record.ErrorMessage = errorMessage;
    }

    private void ClearTrackedDurableExecutions()
    {
        foreach (var record in _dbContext.DurableExecutions.Local.ToArray())
        {
            _dbContext.DurableExecutions.Entry(record).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// 更新时间与乐观并发版本后保存状态变更；对话 Generation 在同一事务中校验。
    /// Saves the state change after updating the time and optimistic concurrency version; the conversation generation is checked in the same transaction.
    /// </summary>
    private async Task SaveStateAsync(
        DurableExecutionRecord record,
        CancellationToken cancellationToken,
        bool preserveExecutionVersion = false
    )
    {
        if (!preserveExecutionVersion)
        {
            record.StateChangedAt = _timeProvider.GetUtcNow();
            record.StateVersion = Guid.CreateVersion7();
        }

        using var userScope = UserInfoUtil.Push(CreateUserPrincipal(record.UserId));
        var task = ToSnapshot(record).Manifest.Task;
        await _dbContext
            .SaveConversationChangesAsync(task.ProjectConversationId, task.Generation, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureSessionCurrentAsync(DurableExecutionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var task = snapshot.Manifest.Task;
        if (
            !await _scopeMaintenance.IsSessionCurrentAsync(
                task.ProjectId,
                task.ProjectConversationId,
                snapshot.Manifest.UserId,
                task.Generation,
                cancellationToken
            )
        )
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }
    }

    private static ClaimsPrincipal CreateUserPrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.Trim())], "DurableExecution"));
}
