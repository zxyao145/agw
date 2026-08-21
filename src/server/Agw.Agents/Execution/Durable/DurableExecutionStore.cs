using Agw.Agents.Execution.Connections;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 从 PostgreSQL 单行状态机还原的 execution 快照。
/// </summary>
internal sealed record DurableExecutionSnapshot
{
    /// <summary>
    /// 获取不可变启动清单。
    /// </summary>
    public required DurableExecutionManifest Manifest { get; init; }

    /// <summary>
    /// 获取数据库中持久化的执行状态。
    /// </summary>
    public required DurableExecutionStatus Status { get; init; }

    /// <summary>
    /// 获取下一次需要执行的分段序号。
    /// </summary>
    public required int SegmentIndex { get; init; }

    /// <summary>
    /// 获取恢复 Agentflow 所需的最新 checkpoint。
    /// </summary>
    public DurableAgentflowCheckpoint? Checkpoint { get; init; }

    /// <summary>
    /// 获取当前等待边界的全部人工请求。
    /// </summary>
    public IReadOnlyList<DurableHumanInteractionSnapshot> PendingInteractions { get; init; } = [];

    /// <summary>
    /// 获取当前等待边界已经持久化的人工回答。
    /// </summary>
    public IReadOnlyList<DurableHumanResponseEnvelope> Responses { get; init; } = [];

    /// <summary>
    /// 获取执行失败时保存的错误信息。
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 返回当前仍未收到回答的人工请求。
    /// </summary>
    public IReadOnlyList<DurableHumanInteractionSnapshot> GetUnansweredInteractions()
    {
        var answered = Responses.Select(item => item.RequestId).ToHashSet(StringComparer.Ordinal);
        return PendingInteractions.Where(item => !answered.Contains(item.RequestId)).ToArray();
    }

    /// <summary>
    /// 从持久化 checkpoint、pending 和 response 构造下一分段输入。
    /// </summary>
    public DurableExecutionSegmentInput CreateSegmentInput()
    {
        var responses = Responses.ToDictionary(item => item.RequestId, StringComparer.Ordinal);
        if (
            responses.Count != Responses.Count
            || responses.Count != PendingInteractions.Count
            || Responses.Any(item => item.ExecutionId != Manifest.ExecutionId)
            || PendingInteractions.Any(item => !responses.ContainsKey(item.RequestId))
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "A resumable execution requires one response for every pending interaction."
            );
        }

        var resolved = PendingInteractions
            .Select(item => new DurableResolvedInteraction(item, responses[item.RequestId]))
            .ToArray();
        return new DurableExecutionSegmentInput(Manifest.ExecutionId, SegmentIndex, resolved, Checkpoint);
    }
}

/// <summary>
/// 在一条 PostgreSQL 记录中原子保存 execution 清单、状态、checkpoint、pending 和 response。
/// </summary>
internal sealed class DurableExecutionStore
{
    private readonly DbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// 创建使用当前 scope DbContext 和统一时钟的 execution 状态仓储。
    /// </summary>
    public DurableExecutionStore(DbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 幂等登记 execution owner 与加密启动清单，并初始化 Queued 状态。
    /// 同 ID 不同内容返回冲突。
    /// </summary>
    internal async Task<DurableExecutionSnapshot> RegisterAsync(
        Guid executionId,
        string userName,
        Guid agentId,
        Agw.Shared.Data.AgentRuntimeType agentType,
        AgwUserInput input,
        TaskProjection task,
        ExecutionSettings settings,
        CancellationToken cancellationToken
    ) =>
        await RegisterAsync(
                executionId,
                userName,
                Constants.AdminUserId,
                agentId,
                agentType,
                input,
                task,
                settings,
                cancellationToken
            )
            .ConfigureAwait(false);

    internal async Task<DurableExecutionSnapshot> RegisterAsync(
        Guid executionId,
        string userName,
        string userId,
        Guid agentId,
        Agw.Shared.Data.AgentRuntimeType agentType,
        AgwUserInput input,
        TaskProjection task,
        ExecutionSettings settings,
        CancellationToken cancellationToken
    )
    {
        if (executionId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "executionId is required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        userId = string.IsNullOrWhiteSpace(userId) ? Constants.AdminUserId : userId.Trim();
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(settings);

        var manifest = new DurableExecutionManifest
        {
            ExecutionId = executionId,
            UserId = userId,
            UserName = userName,
            AgentId = agentId,
            AgentType = agentType,
            Input = input,
            Task = DurableProjectTaskSnapshot.FromProjection(task),
            Settings = DurableExecutionSettings.FromSettings(settings),
        };
        var manifestJson = DurableExecutionJson.Serialize(manifest);
        var existing = await FindAsync(executionId, userName: null, tracking: false, cancellationToken)
            .ConfigureAwait(false);
        if (existing != null)
        {
            return EnsureIdempotentRegistration(existing, userName, manifestJson);
        }

        var now = _timeProvider.GetUtcNow();
        var record = new DurableExecutionRecord
        {
            Id = executionId,
            UserName = userName,
            CreateBy = userId,
            UpdateBy = userId,
            ManifestJson = manifestJson,
            Status = DurableExecutionStatus.Queued,
            SegmentIndex = 0,
            StateChangedAt = now,
            StateVersion = Guid.CreateVersion7(),
        };
        _dbContext.Add(record);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // 多个 Server 可能同时登记同一 executionId；主键选出胜者后再校验真正幂等。
            _dbContext.ChangeTracker.Clear();
            existing = await FindAsync(executionId, userName: null, tracking: false, cancellationToken)
                .ConfigureAwait(false);
            if (existing == null)
            {
                throw;
            }

            return EnsureIdempotentRegistration(existing, userName, manifestJson);
        }

        return ToSnapshot(record);
    }

    /// <summary>
    /// 按 executionId 加载 execution 快照，供受信任的后台执行器使用。
    /// </summary>
    internal async Task<DurableExecutionSnapshot> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var record =
            await FindAsync(executionId, userName: null, tracking: false, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        return ToSnapshot(record);
    }

    /// <summary>
    /// 按 executionId 和 owner 同时加载快照，避免向其他用户泄露执行是否存在。
    /// </summary>
    internal async Task<DurableExecutionSnapshot> GetAuthorizedAsync(
        Guid executionId,
        string userName,
        CancellationToken cancellationToken
    )
    {
        var record =
            await FindAsync(executionId, userName, tracking: false, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        return ToSnapshot(record);
    }

    /// <summary>
    /// 查询等待执行、等待恢复或可能因 Server 退出而遗留的 Running execution。
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> GetRunnableExecutionIdsAsync(
        DateTimeOffset staleRunningBefore,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit <= 0)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "limit must be positive.");
        }

        var candidates = _dbContext
            .Set<DurableExecutionRecord>()
            .AsNoTracking()
            .Where(item =>
                item.Status == DurableExecutionStatus.Queued
                || item.Status == DurableExecutionStatus.Resuming
                || item.Status == DurableExecutionStatus.Running
            );
        if (
            string.Equals(
                _dbContext.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.Sqlite",
                StringComparison.Ordinal
            )
        )
        {
            // SQLite 不能翻译 DateTimeOffset 的比较和排序；它只用于本地/测试，先缩小到可运行状态再在内存筛选。
            var localCandidates = await candidates
                .Select(item => new
                {
                    item.Id,
                    item.Status,
                    item.StateChangedAt,
                })
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            return localCandidates
                .Where(item =>
                    item.Status != DurableExecutionStatus.Running || item.StateChangedAt <= staleRunningBefore
                )
                .OrderBy(item => item.Status == DurableExecutionStatus.Running ? 1 : 0)
                .ThenBy(item => item.StateChangedAt)
                .Select(item => item.Id)
                .Take(limit)
                .ToArray();
        }

        return await candidates
            .Where(item => item.Status != DurableExecutionStatus.Running || item.StateChangedAt <= staleRunningBefore)
            .OrderBy(item => item.Status == DurableExecutionStatus.Running ? 1 : 0)
            .ThenBy(item => item.StateChangedAt)
            .Select(item => item.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 在持有 execution 分布式锁后，把可运行状态转换为 Running 并返回稳定的分段输入快照。
    /// 状态已被其他操作推进时返回 <see langword="null"/>。
    /// </summary>
    internal async Task<DurableExecutionSnapshot?> TryBeginSegmentAsync(
        Guid executionId,
        DateTimeOffset staleRunningBefore,
        CancellationToken cancellationToken
    )
    {
        _dbContext.ChangeTracker.Clear();
        var record =
            await FindAsync(executionId, userName: null, tracking: true, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        var runnable =
            record.Status is DurableExecutionStatus.Queued or DurableExecutionStatus.Resuming
            || record.Status == DurableExecutionStatus.Running && record.StateChangedAt <= staleRunningBefore;
        if (!runnable)
        {
            return null;
        }

        record.Status = DurableExecutionStatus.Running;
        record.ErrorMessage = null;
        try
        {
            await SaveStateAsync(record, cancellationToken).ConfigureAwait(false);
            return ToSnapshot(record);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 中断请求可能在获取锁前后更新并发版本；让下一轮按最新状态重新判断。
            _dbContext.ChangeTracker.Clear();
            return null;
        }
    }

    /// <summary>
    /// 原子持久化一个分段的 checkpoint、pending 或终态。
    /// </summary>
    internal async Task<DurableExecutionSnapshot> SaveSegmentResultAsync(
        DurableExecutionSegmentResult result,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(result);
        _dbContext.ChangeTracker.Clear();
        var record =
            await FindAsync(result.ExecutionId, userName: null, tracking: true, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        if (record.Status != DurableExecutionStatus.Running || record.SegmentIndex != result.SegmentIndex)
        {
            if (record.Status == DurableExecutionStatus.Interrupted)
            {
                return ToSnapshot(record);
            }

            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "The persisted execution state does not match the completed segment."
            );
        }

        ApplySegmentResult(record, result);

        try
        {
            await SaveStateAsync(record, cancellationToken).ConfigureAwait(false);
            return ToSnapshot(record);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveConcurrentSegmentResultAsync(result.ExecutionId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 校验 pending request 后持久化人工回答；全部回答到齐时把状态推进到 Resuming。
    /// </summary>
    internal async Task<DurableExecutionSnapshot> SubmitHumanResponseAsync(
        SubmitDurableHumanResponseRequest request,
        string userName,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = request.RequestId.Trim();
        _dbContext.ChangeTracker.Clear();
        var record =
            await FindAsync(request.ExecutionId, userName, tracking: true, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        var snapshot = ToSnapshot(record);
        var response = new DurableHumanResponseEnvelope
        {
            ExecutionId = request.ExecutionId,
            RequestId = requestId,
            Approved = request.Approved,
            ResponseText = request.ResponseText,
            ApprovalScope = string.IsNullOrWhiteSpace(request.ApprovalScope) ? "once" : request.ApprovalScope.Trim(),
            ResponseData = request.ResponseData,
        };
        var existing = snapshot.Responses.SingleOrDefault(item =>
            string.Equals(item.RequestId, requestId, StringComparison.Ordinal)
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
                string.Equals(item.RequestId, requestId, StringComparison.Ordinal)
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
            _dbContext.ChangeTracker.Clear();
            var current = await GetAuthorizedAsync(request.ExecutionId, userName, cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == DurableExecutionStatus.Interrupted)
            {
                throw new AgwException(ErrorCodes.HumanInteractionNotFound);
            }

            throw;
        }
    }

    /// <summary>
    /// 持久请求中断 execution，并通过并发版本阻止正在运行的分段覆盖 Interrupted 终态。
    /// </summary>
    internal async Task<bool> RequestInterruptAsync(
        Guid executionId,
        string userName,
        CancellationToken cancellationToken
    )
    {
        var now = _timeProvider.GetUtcNow();
        var updatedCount = await _dbContext
            .Set<DurableExecutionRecord>()
            .Where(item =>
                item.Id == executionId
                && item.UserName == userName
                && item.Status != DurableExecutionStatus.Completed
                && item.Status != DurableExecutionStatus.Failed
                && item.Status != DurableExecutionStatus.Interrupted
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(item => item.Status, DurableExecutionStatus.Interrupted)
                        .SetProperty(item => item.StateChangedAt, now)
                        .SetProperty(item => item.StateVersion, Guid.CreateVersion7()),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (updatedCount > 0)
        {
            return true;
        }

        var existing =
            await FindAsync(executionId, userName, tracking: false, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        return false;
    }

    /// <summary>
    /// 加载可选 owner 约束下的 execution 记录。
    /// </summary>
    private Task<DurableExecutionRecord?> FindAsync(
        Guid executionId,
        string? userName,
        bool tracking,
        CancellationToken cancellationToken
    )
    {
        IQueryable<DurableExecutionRecord> query = _dbContext.Set<DurableExecutionRecord>();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }
        if (userName != null)
        {
            query = query.Where(item => item.UserName == userName);
        }

        return query.SingleOrDefaultAsync(item => item.Id == executionId, cancellationToken);
    }

    /// <summary>
    /// 校验并发登记是否与既有 owner 和启动清单完全一致。
    /// </summary>
    private static DurableExecutionSnapshot EnsureIdempotentRegistration(
        DurableExecutionRecord existing,
        string userName,
        string manifestJson
    )
    {
        // ManifestJson 已由 EF 加密拦截器解密；比较规范化明文即可确认请求幂等。
        if (
            !string.Equals(existing.UserName, userName, StringComparison.Ordinal)
            || !string.Equals(existing.ManifestJson, manifestJson, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.DurableExecutionConflict);
        }

        return ToSnapshot(existing);
    }

    /// <summary>
    /// 把分段结果映射到同一条 execution 状态记录。
    /// </summary>
    private static void ApplySegmentResult(DurableExecutionRecord record, DurableExecutionSegmentResult result)
    {
        switch (result.Status)
        {
            case DurableExecutionSegmentStatus.WaitingForHuman:
                ValidatePendingInteractions(result.PendingInteractions);
                record.Status = DurableExecutionStatus.WaitingForHuman;
                record.SegmentIndex = checked(result.SegmentIndex + 1);
                record.CheckpointJson =
                    result.Checkpoint == null ? null : DurableExecutionJson.Serialize(result.Checkpoint);
                record.PendingInteractionsJson = DurableExecutionJson.Serialize(result.PendingInteractions);
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

    /// <summary>
    /// 校验等待边界包含非空且互不重复的 requestId。
    /// </summary>
    private static void ValidatePendingInteractions(IReadOnlyList<DurableHumanInteractionSnapshot> pending)
    {
        var distinct = pending.Select(item => item.RequestId).Distinct(StringComparer.Ordinal).Count();
        if (
            pending.Count == 0
            || distinct != pending.Count
            || pending.Any(item => string.IsNullOrWhiteSpace(item.RequestId) || item.RequestId.Length > 128)
        )
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "A waiting durable segment requires valid, unique pending interactions."
            );
        }
    }

    /// <summary>
    /// 在并发中断更新导致结果保存失败时，以中断状态收敛；其他并发修改视为冲突。
    /// </summary>
    private async Task<DurableExecutionSnapshot> ResolveConcurrentSegmentResultAsync(
        Guid executionId,
        CancellationToken cancellationToken
    )
    {
        _dbContext.ChangeTracker.Clear();
        var record =
            await FindAsync(executionId, userName: null, tracking: true, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.DurableExecutionNotFound);
        if (record.Status != DurableExecutionStatus.Interrupted)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "The distributed execution state changed while a segment result was being saved."
            );
        }
        return ToSnapshot(record);
    }

    /// <summary>
    /// 把记录转换为已解密且经过 schema 校验的 execution 快照。
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

        return new DurableExecutionSnapshot
        {
            Manifest = manifest,
            Status = record.Status,
            SegmentIndex = record.SegmentIndex,
            Checkpoint = string.IsNullOrWhiteSpace(record.CheckpointJson)
                ? null
                : DurableExecutionJson.DeserializeRequired<DurableAgentflowCheckpoint>(
                    record.CheckpointJson,
                    "durable execution checkpoint"
                ),
            PendingInteractions = string.IsNullOrWhiteSpace(record.PendingInteractionsJson)
                ? []
                : DurableExecutionJson.DeserializeRequired<DurableHumanInteractionSnapshot[]>(
                    record.PendingInteractionsJson,
                    "durable execution pending interactions"
                ),
            Responses = string.IsNullOrWhiteSpace(record.ResponsesJson)
                ? []
                : DurableExecutionJson.DeserializeRequired<DurableHumanResponseEnvelope[]>(
                    record.ResponsesJson,
                    "durable execution responses"
                ),
            ErrorMessage = record.ErrorMessage,
        };
    }

    /// <summary>
    /// 设置终态并清除仅恢复期间需要的 checkpoint、pending 和 response。
    /// </summary>
    private static void SetTerminal(DurableExecutionRecord record, DurableExecutionStatus status, string? errorMessage)
    {
        record.Status = status;
        record.CheckpointJson = null;
        record.PendingInteractionsJson = null;
        record.ResponsesJson = null;
        record.ErrorMessage = errorMessage;
    }

    /// <summary>
    /// 更新时间与乐观并发版本后保存状态变更。
    /// </summary>
    private async Task SaveStateAsync(DurableExecutionRecord record, CancellationToken cancellationToken)
    {
        record.StateChangedAt = _timeProvider.GetUtcNow();
        record.StateVersion = Guid.CreateVersion7();
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
