using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Runtime;
using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Outbound.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.History;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Agw.Agents.Application.Persistence.DurableExecutionQueries;

namespace Agw.Agents.Execution.Runtimes.Durable;

/// <summary>
/// Durable 执行的协调：受理实例有并发名额时领取租约并执行，失联或排队的记录由 Worker 领取。全部执行写入经租约检查事务提交，
/// 事件在提交后发布到本实例广播与 Redis 投影，订阅从已提交事件读取。
/// Coordinates durable execution: the accepting instance claims a lease when capacity is available, and lost or queued records are claimed by the worker. Every execution write commits through a lease-checked transaction,
/// events are published to this instance's broadcast and the Redis projection after commit, and subscriptions read committed events.
/// </summary>
internal sealed class DurableExecutionCoordinator : IExecutionCoordinator
{
    private static readonly TimeSpan StatusPollingInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDurableExecutionLeases _leases;
    private readonly DurableExecutionEventLog _eventLog;
    private readonly TurnBroadcastRegistry _broadcasts;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DurableExecutionCoordinator> _logger;
    private readonly DistributedExecutionOptions _options;
    private readonly TimeSpan _streamPollingInterval;
    private readonly DurableSegmentScheduler? _scheduler;
    private readonly AgentflowCheckpointStore? _checkpointStore;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _localExecutions = new();

    public DurableExecutionCoordinator(
        IServiceScopeFactory scopeFactory,
        IDurableExecutionLeases leases,
        DurableExecutionEventLog eventLog,
        TurnBroadcastRegistry broadcasts,
        TimeProvider timeProvider,
        IOptions<ExecutionRuntimeOptions> options,
        ILogger<DurableExecutionCoordinator> logger,
        DurableSegmentScheduler? scheduler = null,
        AgentflowCheckpointStore? checkpointStore = null
    )
    {
        _scopeFactory = scopeFactory;
        _leases = leases;
        _eventLog = eventLog;
        _broadcasts = broadcasts;
        _timeProvider = timeProvider;
        _logger = logger;
        _scheduler = scheduler;
        _checkpointStore = checkpointStore;
        _options = options.Value.Distributed;
        _streamPollingInterval = TimeSpan.FromMilliseconds(_options.EventStream.ReadPollingMilliseconds);
    }

    private TimeSpan LeaseDuration => TimeSpan.FromSeconds(_options.LeaseSeconds);

    /// <summary>
    /// 受理事务登记 Queued；调度器取得执行名额后领取租约。
    /// Acceptance registers Queued; the scheduler claims a lease after reserving execution capacity.
    /// </summary>
    public DurableTurnRegistration CreateRegistration(ExecutionRequest request, AgwMessage start)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Stream)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Distributed execution requires ExecCommand.stream=true.");
        }
        return new DurableTurnRegistration
        {
            UserId = request.UserId,
            ManifestJson = DurableExecutionStore.CreateManifestJson(request),
            WorkerId = null,
            LeaseDuration = LeaseDuration,
            StartEventId = Guid.CreateVersion7(),
            StartEventPayloadJson = JsonUtil.Serialize(start),
        };
    }

    /// <summary>
    /// 受理实例有名额时立即领取执行；排队记录由 Worker 领取。
    /// The accepting instance claims execution immediately when capacity is available; workers claim queued records.
    /// </summary>
    public async Task<ExecutionReceipt> StartAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_scheduler != null)
            await _scheduler.TryStartAsync(request.TurnId, cancellationToken).ConfigureAwait(false);
        return new ExecutionReceipt(request.TurnId, Accepted: true);
    }

    /// <summary>
    /// 开始消息写出后、执行开始前的失败：在同一事务中把执行与 Turn 写为失败，并提交错误与结束事件；提交后广播。
    /// 执行已经被中断等命令结束时，结束事件已经存在，不再写入。
    /// A failure after the start message and before execution: writes the execution and turn as failed and commits the error and finish events in one transaction, broadcasting after commit.
    /// When a command such as an interrupt already ended the execution, its finish event exists and nothing more is written.
    /// </summary>
    public async Task FailAcceptedAsync(
        ExecutionRequest request,
        Exception failure,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var errorCode = TurnMessageFactory.GetErrorCode(AgwTurnStatus.Failed, failure);
        var events = new[]
        {
            PendingExecutionEvent.Create(CreateFailureMessage(failure)),
            PendingExecutionEvent.Create(
                TurnMessageFactory.CreateFinished(request.Envelope, AgwTurnStatus.Failed, 0, errorCode)
            ),
        };
        async Task<IReadOnlyList<TurnBroadcastEntry>> WriteAsync(IServiceProvider services, CancellationToken token)
        {
            if (
                !await services
                    .GetRequiredService<DurableExecutionStore>()
                    .FailAcceptedAsync(request.TurnId, failure.Message, token)
                    .ConfigureAwait(false)
            )
                return [];
            await services
                .GetRequiredService<IConversationTurnStore>()
                .FinishAsync(request.TurnId, ConversationTurnStatus.Failed, 0, errorCode, token)
                .ConfigureAwait(false);
            return await DurableExecutionEvents
                .AppendAsync(
                    services.GetRequiredService<IAgentsDbContext>(),
                    request.TurnId,
                    request.Lease?.Epoch ?? 0,
                    0,
                    events,
                    token
                )
                .ConfigureAwait(false);
        }

        using var ownership = new CancellationTokenSource();
        var committed = request.Lease is { } lease
            ? await _leases.CreateGuard(lease, ownership).RunAsync(WriteAsync, cancellationToken).ConfigureAwait(false)
            : await _leases.RunLockedAsync(request.TurnId, WriteAsync, cancellationToken).ConfigureAwait(false);
        await PublishAsync(request.TurnId, request.UserId, committed).ConfigureAwait(false);
    }

    public async Task<DurableExecutionOutcome> GetOutcomeAsync(
        Guid executionId,
        string userId,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        return await store.GetAuthorizedOutcomeAsync(executionId, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPermissionModeAsync(
        Guid executionId,
        string userId,
        AgwPermissionMode mode,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        await store.SetPermissionModeAsync(executionId, userId, mode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 由 Store 在短时控制锁内校验并原子提交回答，不等待运行中的分段。
    /// The store validates and atomically commits the answer under a short control lock without waiting for a running segment.
    /// </summary>
    public async Task SubmitHumanResponseAsync(
        SubmitDurableHumanResponseRequest request,
        string userId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExecutionId == Guid.Empty || string.IsNullOrWhiteSpace(request.Response.InteractionId))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "executionId and interactionId are required.");
        }
        if (request.Response.InteractionId.Trim().Length > 128)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "interactionId is too long.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        await store.SubmitHumanResponseAsync(request, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DurableExecutionStatusResponse> GetStatusAsync(
        Guid executionId,
        string userId,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await GetSnapshotAsync(executionId, userId, cancellationToken).ConfigureAwait(false);
        return ToStatus(snapshot);
    }

    /// <summary>
    /// 在锁定执行行的事务中写入 Interrupted、Turn 结局与结束事件；运行中的实例之后的写入都会被租约检查拒绝，本实例的执行立即取消。
    /// Writes Interrupted, the turn outcome and the finish event in a transaction that locks the execution row; later writes of a running instance fail the lease check, and a local execution is cancelled at once.
    /// </summary>
    public async Task<bool> InterruptAsync(
        Guid executionId,
        string userId,
        string? reason,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await GetSnapshotAsync(executionId, userId, cancellationToken).ConfigureAwait(false);
        var finished = PendingExecutionEvent.Create(
            TurnMessageFactory.CreateFinished(CreateEnvelope(snapshot.Manifest), AgwTurnStatus.Interrupted, 0, null)
        );
        var (interrupted, committed) = await _leases
            .RunLockedAsync(
                executionId,
                async (services, token) =>
                {
                    if (
                        !await services
                            .GetRequiredService<DurableExecutionStore>()
                            .InterruptAsync(executionId, userId, token)
                            .ConfigureAwait(false)
                    )
                        return (false, (IReadOnlyList<TurnBroadcastEntry>)[]);
                    await services
                        .GetRequiredService<IConversationTurnStore>()
                        .FinishAsync(executionId, ConversationTurnStatus.Interrupted, 0, null, token)
                        .ConfigureAwait(false);
                    return (
                        true,
                        await DurableExecutionEvents
                            .AppendAsync(
                                services.GetRequiredService<IAgentsDbContext>(),
                                executionId,
                                0,
                                snapshot.SegmentIndex,
                                [finished],
                                token
                            )
                            .ConfigureAwait(false)
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!interrupted)
            return false;
        if (_localExecutions.TryGetValue(executionId, out var local))
            await local.CancelAsync().ConfigureAwait(false);
        await PublishAsync(executionId, userId, committed).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 原子截断历史并登记新的恢复分支；来源执行必须已经结束。
    /// Atomically truncates history and registers a new resume branch; the source execution must have finished.
    /// </summary>
    public async Task ResumeCheckpointAsync(
        Guid occurrenceId,
        Guid resumeExecutionId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        string userId,
        CancellationToken cancellationToken,
        DurableExecutionSettings? permissionSettings = null
    )
    {
        var checkpointStore =
            _checkpointStore
            ?? throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Agentflow checkpoint services are not configured."
            );
        await checkpointStore
            .PrepareDistributedResumeAsync(
                occurrenceId,
                resumeExecutionId,
                projectId,
                contextId,
                agentflowId,
                userId,
                cancellationToken,
                permissionSettings
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 鉴权后从游标之后读取已提交的事件：本实例保留该 Turn 时读取广播缓冲，缺少的部分从事件记录补齐。结束事件之后停止。
    /// After authorization, reads committed events after the cursor: this instance's broadcast buffer when it keeps the turn, filling missing parts from the event log. Stops after the finish event.
    /// </summary>
    internal async IAsyncEnumerable<TurnBroadcastEntry> ReadAsync(
        Guid executionId,
        string userId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        _ = await GetSnapshotAsync(executionId, userId, cancellationToken).ConfigureAwait(false);
        var cursor = afterSequence;
        var nextStatusCheck = _timeProvider.GetUtcNow() + StatusPollingInterval;
        while (!cancellationToken.IsCancellationRequested)
        {
            var broadcast = _broadcasts.Find(executionId, userId);
            Task changed = Task.CompletedTask;
            var entries = broadcast?.ReadAfter(cursor, out changed) ?? [];
            if (entries.Count == 0)
                entries = await _eventLog.ReadAsync(executionId, cursor, cancellationToken).ConfigureAwait(false);
            var delivered = false;
            foreach (var entry in entries)
            {
                if (entry.Sequence != cursor + 1)
                    break;
                cursor = entry.Sequence;
                delivered = true;
                yield return entry;
                if (AgwMessageClassifier.IsTurnFinished(entry.Message))
                    yield break;
            }
            if (delivered)
                continue;

            var now = _timeProvider.GetUtcNow();
            if (now >= nextStatusCheck)
            {
                nextStatusCheck = now + StatusPollingInterval;
                var snapshot = await GetSnapshotAsync(executionId, userId, cancellationToken).ConfigureAwait(false);
                if (IsTerminal(snapshot.Status))
                {
                    // 终态与结束事件在同一事务提交；记录被隔离时没有结束事件，按持久状态补发一次。
                    // Terminal states commit with their finish event; a quarantined record has none, so one is issued from the persisted state.
                    var tail = await _eventLog.ReadAsync(executionId, cursor, cancellationToken).ConfigureAwait(false);
                    if (tail.Count > 0)
                        continue;
                    yield return new TurnBroadcastEntry(
                        cursor,
                        TurnMessageFactory.CreateFinished(
                            CreateEnvelope(snapshot.Manifest),
                            ToTurnStatus(snapshot.Status),
                            0,
                            null
                        )
                    );
                    yield break;
                }
            }

            // 本实例保留该 Turn 时由广播唤醒；否则按轮询间隔读取事件记录。
            // With this instance keeping the turn the broadcast wakes the reader; otherwise the event log is polled.
            var delay = Task.Delay(_streamPollingInterval, _timeProvider, cancellationToken);
            await (broadcast == null ? delay : Task.WhenAny(changed, delay)).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing
            );
        }
    }

    /// <summary>
    /// 执行持有租约的一个 Segment：续期租约，执行并在租约检查事务中提交结果与事件。失去租约、被中断或实例关闭时停止，不提交结果。
    /// Runs one lease-holding segment: renews the lease, executes, and commits the result and events in a lease-checked transaction. Losing the lease, an interrupt or instance shutdown stops it without committing a result.
    /// </summary>
    internal async Task RunLeasedSegmentAsync(DurableLease lease, CancellationToken stoppingToken)
    {
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_localExecutions.TryAdd(lease.ExecutionId, ownership))
            return;
        try
        {
            DurableExecutionSnapshot? snapshot;
            await using (var scope = _scopeFactory.CreateAsyncScope())
            using (UserInfoUtil.PushSystemScope())
            {
                snapshot = await scope
                    .ServiceProvider.GetRequiredService<DurableExecutionStore>()
                    .LoadClaimedAsync(lease.ExecutionId, ownership.Token)
                    .ConfigureAwait(false);
            }
            if (snapshot == null)
                return;
            using var owner = UserInfoUtil.Push(CreateUserPrincipal(snapshot.Manifest.UserId));
            var guard = _leases.CreateGuard(lease, ownership);
            using var renewalStop = new CancellationTokenSource();
            var renewal = RenewLeaseAsync(lease, ownership, renewalStop.Token);
            try
            {
                DurableExecutionSegmentResult result;
                var reportedFailure = true;
                try
                {
                    result = await ExecuteSegmentAsync(snapshot, lease, guard, ownership.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ownership.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception) when (!ownership.IsCancellationRequested)
                {
                    _logger.LogError(
                        exception,
                        "Durable execution {ExecutionId} segment {SegmentIndex} failed.",
                        lease.ExecutionId,
                        snapshot.SegmentIndex
                    );
                    reportedFailure = false;
                    result = new DurableExecutionSegmentResult
                    {
                        ExecutionId = lease.ExecutionId,
                        SegmentIndex = snapshot.SegmentIndex,
                        Status = DurableExecutionSegmentStatus.Failed,
                        ErrorMessage = exception.Message,
                        ErrorCode = TurnMessageFactory.GetErrorCode(AgwTurnStatus.Failed, exception),
                    };
                }
                await CommitResultAsync(snapshot, lease, guard, result, reportedFailure, ownership.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                await renewalStop.CancelAsync().ConfigureAwait(false);
                await renewal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            _localExecutions.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(lease.ExecutionId, ownership));
        }
    }

    /// <summary>
    /// 在本实例执行一个 Segment：按持久化清单重建执行上下文与作用域，调用对应的 TurnExecutor；普通输出经事件输出提交。
    /// Runs one segment on this instance: rebuilds the execution context and scope from the persisted manifest and invokes the matching TurnExecutor; ordinary output commits through the event output.
    /// </summary>
    internal async Task<DurableExecutionSegmentResult> ExecuteSegmentAsync(
        DurableExecutionSnapshot snapshot,
        DurableLease lease,
        IExecutionWriteGuard guard,
        CancellationToken ownershipLost
    )
    {
        var input = snapshot.CreateSegmentInput();
        var manifest = snapshot.Manifest;
        await using var serviceScope = _scopeFactory.CreateAsyncScope();
        var services = serviceScope.ServiceProvider;
        var task = manifest.Task.ToProjection();
        var context = await services
            .GetRequiredService<ExecutionContextFactory>()
            .CreateAsync(
                new ExecutionContextRequest(
                    manifest.UserId,
                    input.ExecutionId,
                    manifest.AgentType,
                    manifest.AgentId,
                    task,
                    manifest.WorkspaceSnapshot!,
                    manifest.Settings.PermissionMode,
                    manifest.Settings.PermissionVersion,
                    ExecutionProvider.Distributed
                ),
                ownershipLost
            )
            .ConfigureAwait(false);
        ProjectWorkspacePaths.EnsureAvailable(manifest.Task.ProjectId, manifest.WorkspaceSnapshot!);
        await using var sink = new DurableEventSink(
            guard,
            lease,
            input.SegmentIndex,
            _broadcasts.GetOrCreate(input.ExecutionId, manifest.UserId),
            _eventLog,
            _options.EventStream,
            _timeProvider,
            ownershipLost
        );
        var turn = new TurnRecord(_scopeFactory, input.ExecutionId, GetInputMessageId(manifest), guard);
        var scope = ExecutionScope.Create(
            context,
            task.TaskId,
            sink,
            new DurablePendingInteractionSet(context.PermissionMode, RestoreInteractions(input)),
            new InMemoryTurnCheckpointStore(),
            turn,
            guard
        );
        // Segment 的历史缓冲经写入入口提交，失去所有权时停止写入。
        // The segment's history buffer commits through the write guard and stops writing once ownership is lost.
        var history = services
            .GetService<IConversationHistoryStore>()
            ?.BeginBuffer(
                new ConversationHistoryScope
                {
                    ProjectId = manifest.Task.ProjectId,
                    ContextId = manifest.Task.ContextId,
                    Generation = manifest.Task.Generation,
                    IsExecutionBound = true,
                },
                ownershipLost,
                guard
            );
        if (history != null)
            scope.BindHistory(history);
        try
        {
            using var executionScope = scope.Push();
            await turn.MarkRunningAsync(ownershipLost).ConfigureAwait(false);
            return manifest.AgentType switch
            {
                AgentRuntimeType.Agent => await RunAgentSegmentAsync(
                        scope,
                        manifest,
                        input,
                        sink,
                        services.GetRequiredService<IAgentRuntimeFactory>(),
                        services.GetRequiredService<AgentTurnExecutor>(),
                        ownershipLost
                    )
                    .ConfigureAwait(false),
                AgentRuntimeType.Agentflow => await RunAgentflowSegmentAsync(
                        scope,
                        manifest,
                        input,
                        sink,
                        services.GetRequiredService<AgentflowTurnExecutor>(),
                        ownershipLost
                    )
                    .ConfigureAwait(false),
                _ => throw new AgwException(
                    ErrorCodes.UnsupportedAgentType,
                    $"Agent runtime type '{manifest.AgentType}' is not supported."
                ),
            };
        }
        catch (Exception exception)
        {
            scope.RecordFailure(exception);
            throw;
        }
        finally
        {
            if (history != null)
                await history.CompleteAsync(scope.Failure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 在已建立的执行作用域内执行一个 Agent Segment；每个 Segment 重建 Runtime，从保存的会话与回答继续。
    /// Runs one Agent segment inside the established execution scope; each segment rebuilds the Runtime and continues from the saved session and answers.
    /// </summary>
    internal static Task<DurableExecutionSegmentResult> RunAgentSegmentAsync(
        ExecutionScope scope,
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        IAgentRuntimeFactory agentRuntimes,
        AgentTurnExecutor agentTurns,
        CancellationToken cancellationToken
    ) =>
        RunSegmentAsync(
            scope,
            manifest,
            input,
            sink,
            async (registry, token) =>
            {
                var runtime =
                    await agentRuntimes
                        .CreateRuntimeAsync(
                            manifest.AgentId,
                            manifest.Task.ToProjection(),
                            manifest.Settings.ToRuntimeSettings(manifest.Task.ProjectId, manifest.Task.ContextId),
                            token
                        )
                        .ConfigureAwait(false)
                    ?? throw new AgwException(ErrorCodes.AgentExecutionFailed, "Agent could not be created.");
                await using (runtime)
                {
                    if (runtime.AgentType == AgentType.External)
                    {
                        throw new AgwException(
                            ErrorCodes.AgentExecutionFailed,
                            "Distributed execution currently supports System Agents only."
                        );
                    }

                    // ResultOnly 只在本轮会产生 result 消息时生效：过滤出站消息并拒绝人机交互。
                    // ResultOnly applies only when the turn produces a result: outbound messages are filtered and interaction is declined.
                    var resultOnly = manifest.Settings.ResultOnly && runtime.EmitsTurnResult;
                    var output = ResultOnlyMessageSink.Wrap(sink, resultOnly);
                    scope.BindInteractions(
                        new PendingInteractionHandler(
                            manifest.Settings.HumanInteractionPolicy,
                            registry,
                            allowInteraction: !resultOnly
                        ),
                        new ResolvedHumanInteractionChannel(scope.Interactions, allowInteraction: !resultOnly),
                        registry
                    );
                    await foreach (
                        var message in agentTurns
                            .RunAsync(scope, runtime, CreateTurnInput(manifest, input), token)
                            .ConfigureAwait(false)
                    )
                    {
                        await output.WriteAsync(message, token).ConfigureAwait(false);
                    }
                }
            },
            cancellationToken
        );

    /// <summary>
    /// 在已建立的执行作用域内执行一个 Agentflow Segment；首个 Segment 启动 Workflow，其余 Segment 从 Workflow 存档恢复。
    /// Runs one Agentflow segment inside the established execution scope; the first segment starts the Workflow and later ones resume from its checkpoint.
    /// </summary>
    internal static Task<DurableExecutionSegmentResult> RunAgentflowSegmentAsync(
        ExecutionScope scope,
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        AgentflowTurnExecutor agentflowTurns,
        CancellationToken cancellationToken
    ) =>
        RunSegmentAsync(
            scope,
            manifest,
            input,
            sink,
            async (registry, token) =>
            {
                if (input.SegmentIndex > 0 && input.Checkpoint == null)
                {
                    throw new AgwException(
                        ErrorCodes.DurableExecutionConflict,
                        "Agentflow checkpoint could not be found."
                    );
                }
                await using var runtime = AgentflowRuntimeFactory.CreateRuntime(
                    manifest.AgentId,
                    manifest.Task.ToProjection(),
                    manifest.Settings.ToRuntimeSettings(manifest.Task.ProjectId, manifest.Task.ContextId),
                    deferHumanInteractions: true
                );
                scope.BindInteractions(
                    new PendingInteractionHandler(manifest.Settings.HumanInteractionPolicy, registry),
                    new ResolvedHumanInteractionChannel(scope.Interactions),
                    registry
                );
                await foreach (
                    var message in agentflowTurns
                        .RunAsync(scope, runtime, CreateTurnInput(manifest, input), token)
                        .ConfigureAwait(false)
                )
                {
                    await sink.WriteAsync(message, token).ConfigureAwait(false);
                }
            },
            cancellationToken
        );

    /// <summary>
    /// 把上一等待边界已保存的输入回答恢复为已答请求，供输入工具按原请求身份读取。
    /// Restores the input answers saved at the previous wait boundary as answered requests that input tools read by their original identity.
    /// </summary>
    internal static PendingInteractionSnapshot RestoreInteractions(DurableExecutionSegmentInput input) =>
        new(
            input
                .ResolvedInputs.Concat(input.ResolvedInteractions.Where(item => item.Request is UserInputInteraction))
                .DistinctBy(item => item.Request.InteractionId)
                .Select(item => new PendingInteractionEntry(
                    $"segment-{input.SegmentIndex}",
                    new InteractionIdentity
                    {
                        InteractionId = item.Request.InteractionId,
                        TurnId = input.ExecutionId,
                        NodeId = item.Request.Source.NodeId ?? InteractionIdentity.StandaloneNodeId,
                        ActivationIndex = 0,
                        StepIndex = 0,
                        ProviderRequestId = item.Request.Source.ProviderRequestId ?? item.Request.InteractionId,
                        CallId = item.Request.Source.CallId,
                    },
                    item.Request,
                    PendingInteractionStatus.Answered,
                    item.Response
                ))
                .ToArray(),
            ApprovalRounds: 0
        );

    /// <summary>
    /// 将持久化快照映射为连接层使用的最小状态响应。
    /// Maps the persisted snapshot to the minimal status response used by the connection layer.
    /// </summary>
    internal static DurableExecutionStatusResponse ToStatus(DurableExecutionSnapshot snapshot) =>
        new(
            snapshot.Manifest.ExecutionId,
            snapshot.Status,
            CreateEnvelope(snapshot.Manifest).StreamingScopeId,
            snapshot.Manifest.Settings.PermissionMode,
            snapshot.Manifest.Settings.NextPermissionMode ?? snapshot.Manifest.Settings.PermissionMode,
            Math.Max(snapshot.Manifest.Settings.NextPermissionVersion, snapshot.Manifest.Settings.PermissionVersion),
            snapshot.Manifest.Settings.PermissionVersion
        );

    /// <summary>
    /// 由启动清单还原开始与结束消息的字段。
    /// Restores the start and finish message fields from the manifest.
    /// </summary>
    internal static TurnEnvelope CreateEnvelope(DurableExecutionManifest manifest) =>
        new(
            manifest.ExecutionId,
            manifest.Task.ProjectConversationId,
            manifest.AgentId,
            manifest.AgentType,
            manifest.StreamingScopeId
                ?? (
                    string.IsNullOrWhiteSpace(manifest.Input.MessageId)
                        ? manifest.ExecutionId.ToString("D")
                        : manifest.Input.MessageId
                )
        );

    /// <summary>
    /// 执行 Segment 并把 TurnExecutor 报告的结局转换为分段结果；执行异常写出错误消息并记为失败。
    /// Runs the segment and converts the outcome reported by the TurnExecutor into a segment result; an execution exception writes an error message and fails the segment.
    /// </summary>
    private static async Task<DurableExecutionSegmentResult> RunSegmentAsync(
        ExecutionScope scope,
        DurableExecutionManifest manifest,
        DurableExecutionSegmentInput input,
        IExecutionMessageSink sink,
        Func<InteractionRequestRegistry, CancellationToken, Task> runTurn,
        CancellationToken cancellationToken
    )
    {
        var registry = new InteractionRequestRegistry(
            input
                .InputCatalog.Concat(
                    input.ResolvedInteractions.Select(item => item.Request).OfType<UserInputInteraction>()
                )
                .DistinctBy(item => item.InteractionId)
        );
        try
        {
            await runTurn(registry, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var outcome =
                scope.Outcome
                ?? throw new AgwException(ErrorCodes.AgentExecutionFailed, "The execution ended without an outcome.");
            if (outcome.Status == TurnOutcomeStatus.Failed)
            {
                scope.RecordFailure(
                    new AgwException(ErrorCodes.AgentExecutionFailed, outcome.ErrorMessage ?? "Agent execution failed.")
                );
            }

            return new DurableExecutionSegmentResult
            {
                ExecutionId = manifest.ExecutionId,
                SegmentIndex = input.SegmentIndex,
                Status = outcome.Status switch
                {
                    TurnOutcomeStatus.Completed => DurableExecutionSegmentStatus.Completed,
                    TurnOutcomeStatus.WaitingForHuman => DurableExecutionSegmentStatus.WaitingForHuman,
                    _ => DurableExecutionSegmentStatus.Failed,
                },
                PendingInteractions = outcome.PendingInteractions,
                InputCatalog = registry.Snapshot(),
                Checkpoint = outcome.Checkpoint,
                ErrorMessage = outcome.ErrorMessage,
                ErrorCode =
                    outcome.Status == TurnOutcomeStatus.Failed
                        ? TurnMessageFactory.GetErrorCode(AgwTurnStatus.Failed, scope.Failure)
                        : null,
                StepCount = outcome.StepCount,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            scope.RecordFailure(exception);
            await sink.WriteAsync(CreateFailureMessage(exception), CancellationToken.None).ConfigureAwait(false);
            return new DurableExecutionSegmentResult
            {
                ExecutionId = manifest.ExecutionId,
                SegmentIndex = input.SegmentIndex,
                Status = DurableExecutionSegmentStatus.Failed,
                ErrorMessage = exception.Message,
                ErrorCode = TurnMessageFactory.GetErrorCode(AgwTurnStatus.Failed, exception),
            };
        }
    }

    /// <summary>
    /// 在租约检查事务中提交分段结果：等待边界与 interaction-request 事件一起提交，终态与 Turn 结局、结束事件一起提交；提交后广播。
    /// Commits a segment result in a lease-checked transaction: a wait boundary commits with its interaction-request events, a terminal state with the turn outcome and finish event; broadcasting follows the commit.
    /// </summary>
    internal async Task CommitResultAsync(
        DurableExecutionSnapshot snapshot,
        DurableLease lease,
        IExecutionWriteGuard guard,
        DurableExecutionSegmentResult result,
        bool failureReported,
        CancellationToken cancellationToken
    )
    {
        var envelope = CreateEnvelope(snapshot.Manifest);
        var events = new List<PendingExecutionEvent>();
        string? terminalStatus = null;
        if (result.Status == DurableExecutionSegmentStatus.WaitingForHuman)
        {
            events.AddRange(
                result.PendingInteractions.Select(interaction =>
                    PendingExecutionEvent.Create(
                        InteractionMessageMapper.Create(
                            interaction,
                            Guid.CreateVersion7().ToString("N"),
                            lease.ExecutionId,
                            envelope.StreamingScopeId
                        )
                    )
                )
            );
        }
        else
        {
            terminalStatus =
                result.Status == DurableExecutionSegmentStatus.Completed
                    ? AgwTurnStatus.Completed
                    : AgwTurnStatus.Failed;
            if (!failureReported)
                events.Add(
                    PendingExecutionEvent.Create(
                        CreateFailureMessage(
                            new AgwException(
                                ErrorCodes.AgentExecutionFailed,
                                result.ErrorMessage ?? "Distributed execution failed."
                            )
                        )
                    )
                );
            events.Add(
                PendingExecutionEvent.Create(
                    TurnMessageFactory.CreateFinished(envelope, terminalStatus, result.StepCount, result.ErrorCode)
                )
            );
        }

        var committed = await guard
            .RunAsync(
                async (services, token) =>
                {
                    await services
                        .GetRequiredService<DurableExecutionStore>()
                        .ApplySegmentResultAsync(result, token)
                        .ConfigureAwait(false);
                    if (terminalStatus != null)
                        await services
                            .GetRequiredService<IConversationTurnStore>()
                            .FinishAsync(
                                lease.ExecutionId,
                                TurnRecord.ToTerminalStatus(terminalStatus),
                                result.StepCount,
                                result.ErrorCode,
                                token
                            )
                            .ConfigureAwait(false);
                    return await DurableExecutionEvents
                        .AppendAsync(
                            services.GetRequiredService<IAgentsDbContext>(),
                            lease.ExecutionId,
                            lease.Epoch,
                            result.SegmentIndex,
                            events,
                            token
                        )
                        .ConfigureAwait(false);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        await PublishAsync(lease.ExecutionId, snapshot.Manifest.UserId, committed).ConfigureAwait(false);
    }

    /// <summary>
    /// 每 LeaseRenewSeconds 续期一次；续期失败说明租约已经失去，取消本地执行。
    /// Renews every LeaseRenewSeconds; a failed renewal means the lease is lost and cancels local execution.
    /// </summary>
    private async Task RenewLeaseAsync(
        DurableLease lease,
        CancellationTokenSource ownership,
        CancellationToken cancellationToken
    )
    {
        var interval = TimeSpan.FromSeconds(_options.LeaseRenewSeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            if (!await _leases.RenewAsync(lease, LeaseDuration, cancellationToken).ConfigureAwait(false))
            {
                await ownership.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// 已提交的事件发布到本实例广播与 Redis 投影。
    /// Publishes committed events to this instance's broadcast and the Redis projection.
    /// </summary>
    private async Task PublishAsync(Guid executionId, string userId, IReadOnlyList<TurnBroadcastEntry> committed)
    {
        if (committed.Count == 0)
            return;
        _broadcasts.Find(executionId, userId)?.PublishCommitted(committed);
        await _eventLog.PublishAsync(executionId, committed, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// 首个 Segment 使用清单中的用户输入；其余 Segment 附带已保存的回答与 Workflow 存档，新恢复分支的首个 Segment 放行清单中的 CheckpointMarker。
    /// The first segment uses the manifest input; later segments carry the saved answers and Workflow checkpoint, and the first segment of a resume branch passes the manifest's CheckpointMarkers.
    /// </summary>
    private static TurnInput CreateTurnInput(DurableExecutionManifest manifest, DurableExecutionSegmentInput input) =>
        input.SegmentIndex == 0 && input.ResolvedInteractions.Count == 0
            ? new TurnInput(manifest.Input)
            : new TurnInput(manifest.Input)
            {
                Resume = new TurnResume
                {
                    ResolvedInteractions = input.ResolvedInteractions,
                    Checkpoint = input.Checkpoint,
                    CheckpointNodeIds =
                        input.SegmentIndex == 1 && manifest.ResumeCheckpointOccurrenceId.HasValue
                            ? manifest.ResumeCheckpointNodeIds.ToHashSet(StringComparer.Ordinal)
                            : new HashSet<string>(StringComparer.Ordinal),
                },
            };

    /// <summary>
    /// 受理时写入的输入行：有输入内容的 Turn 以输入消息 ID 作为行 Id。
    /// The input row written at acceptance: a turn with input content uses the input message ID as the row Id.
    /// </summary>
    private static Guid? GetInputMessageId(DurableExecutionManifest manifest) =>
        manifest.Input.Contents.Count > 0 && Guid.TryParse(manifest.Input.MessageId, out var id) ? id : null;

    private static string ToTurnStatus(DurableExecutionStatus status) =>
        status switch
        {
            DurableExecutionStatus.Failed => AgwTurnStatus.Failed,
            DurableExecutionStatus.Interrupted => AgwTurnStatus.Interrupted,
            _ => AgwTurnStatus.Completed,
        };

    private static AgwMessage CreateFailureMessage(Exception exception) =>
        new(
            Guid.CreateVersion7().ToString("N"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = exception.Message }]
        );

    private async Task<DurableExecutionSnapshot> GetSnapshotAsync(
        Guid executionId,
        string userId,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DurableExecutionStore>();
        return await store.GetAuthorizedAsync(executionId, userId, cancellationToken).ConfigureAwait(false);
    }

    private static ClaimsPrincipal CreateUserPrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "DurableExecution"));
}
