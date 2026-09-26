using System.Security.Claims;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Commands.Checkpoint;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Inbound.Connections;

public sealed class ExecutionConnectionContext : IAsyncDisposable
{
    private const string SetModeAfterTurnActionKey = "set-mode";

    private readonly string _userId;
    private readonly IExecutionMessageSink _messageSink;
    private readonly TurnAcceptanceService _acceptance;
    private readonly IProjectTaskFacade _projectTasks;
    private readonly IProjectDefaultResolver? _projectDefaults;
    private readonly IExecutionCoordinator _coordinator;
    private readonly InProcessExecutionCoordinator? _inProcess;
    private readonly DurableExecutionAttachment? _attachment;
    private readonly AgentflowCheckpointStore? _checkpointStore;
    private InProcessTurnHost? Host => _inProcess?.Host;
    private AgentExecutionTask? _resolvedTask;
    private string? _workspace;
    private ExecutionTarget? _target;
    private PendingModeChange? _pendingModeChange;
    private Guid? _lastResumeExecutionId;
    private volatile bool _waitingForHuman;
    private readonly ExecutionPermissionService? _permissions;
    private ExecutionSettings? _turnSettings;
    private TurnBroadcast? _turnBroadcast;

    /// <summary>
    /// 创建连接上下文；进程内协调器工厂与 Durable 协调器恰好提供一个，决定本连接的执行方式。
    /// Creates the connection context; exactly one of the in-process coordinator factory and the Durable coordinator is supplied, deciding how this connection executes.
    /// </summary>
    internal ExecutionConnectionContext(
        string userId,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken,
        TurnAcceptanceService acceptance,
        IProjectTaskFacade projectTasks,
        InProcessExecutionCoordinatorFactory? inProcessCoordinators,
        DurableExecutionCoordinator? durableCoordinator,
        AgentflowCheckpointStore? checkpointStore = null,
        IProjectDefaultResolver? projectDefaults = null,
        ExecutionPermissionService? permissions = null
    )
    {
        _permissions = permissions;
        _userId = string.IsNullOrWhiteSpace(userId)
            ? throw new AgwException(ErrorCodes.AuthenticationRequired)
            : userId.Trim();
        _messageSink = messageSink;
        _acceptance = acceptance;
        _projectTasks = projectTasks;
        _projectDefaults = projectDefaults;
        _checkpointStore = checkpointStore;
        if ((inProcessCoordinators == null) == (durableCoordinator == null))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Exactly one of the in-process and durable coordinators is required."
            );
        }
        if (durableCoordinator == null)
        {
            _inProcess = inProcessCoordinators!.Create(hostToken, pendingCount => _waitingForHuman = pendingCount > 0);
            _coordinator = _inProcess;
        }
        else
        {
            _attachment = new DurableExecutionAttachment(_userId, messageSink, hostToken, durableCoordinator);
            _coordinator = durableCoordinator;
        }
    }

    public ExecutionSettings? Settings { get; private set; }

    public string UserId => _userId;

    public Guid? ProjectId => _resolvedTask?.ProjectId ?? Settings?.ProjectId;

    public Guid? ProjectConversationId => _resolvedTask?.ProjectConversationId;

    /// <summary>
    /// 连接配置的 Project Conversation；执行、订阅与 checkpoint 操作都必须属于它。
    /// The Project Conversation configured on this connection; executions, subscriptions and checkpoint operations must all belong to it.
    /// </summary>
    public Guid? ConversationId => Settings?.ConversationId;

    public AgentExecutionTask? ResolvedTask => _resolvedTask;

    public string? Workspace => _workspace;

    public ExecutionTarget? Target => _target;

    public Guid? AgentId => _target?.AgentId;

    public AgentRuntimeType? AgentType => _target?.AgentType;

    public bool HasActiveTurn
    {
        get
        {
            if (Host is { HasActiveTurn: true })
            {
                return true;
            }

            return _attachment?.HasActiveExecution == true;
        }
    }

    /// <summary>
    /// 进程内 Turn 已经写出结束消息、只剩释放与 Turn 后动作时为 true；客户端收到结束消息后立即发来的命令等它收尾后受理。
    /// True when an in-process turn has written its finish message and only disposal and after-turn actions remain; a command sent right after the client receives the finish message is accepted once it settles.
    /// </summary>
    private bool IsFinishingTurn => Host is { HasActiveTurn: true } && HasWrittenTurnFinish;

    /// <summary>
    /// 连接上最近的进程内 Turn 已写出结束消息，这条连接会把它送达自己的客户端；之后只剩释放与 Turn 后动作。
    /// The latest in-process turn on this connection has written its finish message, which this connection delivers to its own client; only disposal and after-turn actions remain.
    /// </summary>
    internal bool HasWrittenTurnFinish => _turnBroadcast is { IsFinished: true };

    /// <summary>
    /// 仍在运行的 Turn 拒绝新的执行或设置；正在收尾的进程内 Turn 等待空闲。
    /// A running turn rejects a new execution or new settings; an in-process turn that is settling is awaited until idle.
    /// </summary>
    private async Task EnsureIdleForNewTurnAsync()
    {
        if (!HasActiveTurn)
        {
            return;
        }

        if (!IsFinishingTurn)
        {
            throw new AgwException(ErrorCodes.ExecutionBusy);
        }

        await Host!.WhenIdleAsync();
    }

    public async Task ApplySettingsAsync(ExecutionSettings settings, CancellationToken cancellationToken)
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Equals(Settings))
        {
            // ResultOnly 不参与相等判定，单独切换它时保留 runtime，下一轮生效。
            // ResultOnly is excluded from equality, so toggling it alone keeps the runtime and applies next turn.
            Settings = Settings?.WithResultOnly(settings.ResultOnly);
            return;
        }

        await EnsureIdleForNewTurnAsync();
        await ReleaseRuntimeAsync();
        Settings = settings.WithPermissionSnapshot(
            settings.PermissionMode,
            Settings == null
                ? 0
                : Settings.PermissionVersion + (Settings.PermissionMode == settings.PermissionMode ? 0 : 1)
        );
        _resolvedTask = null;
        _workspace = null;
        _target = null;
        _lastResumeExecutionId = null;
    }

    public async Task StartTurnAsync(ExecCommand command, CancellationToken cancellationToken)
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        ArgumentNullException.ThrowIfNull(command);
        var agentId =
            command.AgentId ?? throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        var conversationId =
            command.ConversationId
            ?? throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.conversationId is required.");
        if (conversationId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.conversationId is required.");
        }
        if (
            (Settings?.ConversationId is { } configuredConversationId && configuredConversationId != conversationId)
            || (_resolvedTask != null && _resolvedTask.ProjectConversationId != conversationId)
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "ExecCommand.conversationId does not match the active conversation."
            );
        }

        if (
            HasActiveTurn
            && _attachment != null
            && command.ExecutionId.HasValue
            && command.ExecutionId == _attachment.ActiveExecutionId
        )
        {
            await SubscribeExecutionAsync(command.ExecutionId.Value, cursor: null, cancellationToken);
            return;
        }

        await EnsureIdleForNewTurnAsync();
        if (Host != null)
        {
            await Host.WhenIdleAsync();
        }

        if (Settings?.ConversationId == null)
        {
            Settings = (Settings ?? ExecutionSettings.CreateDefault()).WithConversationId(conversationId);
        }
        await RefreshResolvedTaskAsync(conversationId, cancellationToken);
        var target = new ExecutionTarget(agentId, command.AgentType);
        var requestedMode =
            command.AgentType == AgentRuntimeType.Agent
            && _pendingModeChange is { } pendingModeChange
            && pendingModeChange.AgentId == agentId
                ? pendingModeChange.Mode
                : null;
        var accepted = await _acceptance.AcceptAsync(
            new TurnAcceptanceRequest(
                _userId,
                command.ExecutionId,
                target,
                conversationId,
                command.Input,
                Settings,
                command.Stream
            )
            {
                Task = _resolvedTask,
                RequestedMode = requestedMode,
                ResumeCheckpoint = command.ResumeCheckpoint,
            },
            cancellationToken
        );
        var request = accepted.Request;
        _resolvedTask = request.Task;
        _workspace = request.WorkspaceSnapshot.Workspace;
        command.ExecutionId = request.TurnId;
        if (!accepted.Created)
        {
            await ResumeAcceptedTurnAsync(accepted, cancellationToken);
            return;
        }

        // 受理事务已经提交：先写出开始消息，再创建 Runtime。
        // The acceptance transaction has committed: the start message goes out before the Runtime is created.
        _turnSettings = Settings;
        if (_attachment != null)
        {
            await _messageSink.WriteAsync(accepted.Start, CancellationToken.None);
        }
        else
        {
            _turnBroadcast = accepted.Broadcast!;
            _turnBroadcast.AddSink(_messageSink);
            await _turnBroadcast.WriteAsync(accepted.Start, CancellationToken.None);
        }
        await SendPermissionStatusAsync(starting: true);
        try
        {
            if (command.ResumeCheckpoint != null && command.ResumeGeneration != request.Task.Generation)
            {
                throw new AgwException(ErrorCodes.ConversationSessionConflict);
            }
            var start = await _coordinator.StartAsync(request, cancellationToken);
            if (!start.Accepted)
            {
                throw new AgwException(ErrorCodes.AgentExecutionFailed, "Agent execution could not be started.");
            }
        }
        catch (Exception exception)
        {
            await _acceptance.ReportStartFailureAsync(accepted, exception);
            if (_attachment != null)
            {
                await _attachment.AttachAsync(request.TurnId, cursor: "1", conversationId, cancellationToken);
            }
            return;
        }

        if (_attachment != null)
        {
            await _attachment.AttachAsync(request.TurnId, cursor: "1", conversationId, cancellationToken);
        }
        _target = target;
        if (requestedMode != null && Host != null)
        {
            _pendingModeChange = null;
            await SendModeStatusAsync(agentId, requestedMode);
        }
    }

    /// <summary>
    /// 同一 turnId 的重发不重复受理：Durable 从头回放已提交的事件；进程内回放本实例保留的广播，Turn 不在本实例时按 Turn 行写出结局。
    /// A resend of the same turnId is not accepted again: Durable replays committed events from the start; in-process replays the broadcast this instance keeps, writing the outcome from the turn row when the turn is not here.
    /// </summary>
    private async Task ResumeAcceptedTurnAsync(AcceptedTurn accepted, CancellationToken cancellationToken)
    {
        if (_attachment != null)
        {
            await _attachment.AttachAsync(
                accepted.Request.TurnId,
                cursor: null,
                accepted.Request.Task.ProjectConversationId,
                cancellationToken
            );
            return;
        }
        if (accepted.Broadcast != null)
        {
            await accepted.Broadcast.AttachAsync(_messageSink, afterSequence: 0);
            return;
        }
        await _messageSink.WriteAsync(
            TurnMessageFactory.CreateFinished(
                accepted.Request.Envelope,
                TurnAcceptanceService.ToFinishedStatus(accepted.Turn.Status),
                accepted.Turn.StepCount,
                accepted.Turn.ErrorCode
            ),
            CancellationToken.None
        );
    }

    public async Task SetModeAsync(Guid agentId, string mode, CancellationToken cancellationToken)
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        var change = new PendingModeChange(agentId, mode);
        _pendingModeChange = change;
        if (
            Host is not { AgentRuntime: not null } host
            || _target is not { AgentId: var targetAgentId }
            || targetAgentId != agentId
        )
        {
            return;
        }

        if (host.TryScheduleAfterTurn(SetModeAfterTurnActionKey, _ => ApplyQueuedModeAsync(host, change)))
        {
            return;
        }

        await _inProcess!.SetModeAsync(mode, cancellationToken);
        if (_pendingModeChange == change)
        {
            _pendingModeChange = null;
        }

        await SendModeStatusAsync(agentId, mode);
    }

    public async Task SetPermissionModeAsync(AgwPermissionMode permissionMode, CancellationToken cancellationToken)
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        if (_permissions != null && HasActiveTurn && _target is { } target)
            ExecutionPermissionService.Validate(
                await _permissions.GetAsync(target.AgentType, target.AgentId, cancellationToken),
                permissionMode
            );
        if (_attachment != null)
            await _attachment.SetPermissionModeAsync(permissionMode, cancellationToken);
        Settings = (Settings ?? ExecutionSettings.CreateDefault()).WithPermissionMode(permissionMode);
        await SendPermissionStatusAsync();
    }

    /// <summary>
    /// 中断当前连接内的活动执行；进程内模式无需显式 executionId。
    /// </summary>
    public Task InterruptTurnAsync(string? reason, CancellationToken cancellationToken) =>
        InterruptTurnAsync(executionId: null, reason, cancellationToken);

    /// <summary>
    /// 中断指定 durable execution；进程内模式仍退化为中断当前 Turn。
    /// </summary>
    public async Task InterruptTurnAsync(Guid? executionId, string? reason, CancellationToken cancellationToken)
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        if (_attachment != null)
        {
            await _attachment.InterruptAsync(executionId, reason, ConversationId, cancellationToken);
            return;
        }

        if (!HasActiveTurn)
        {
            await SendSystemMessageAsync(reason ?? "No active request is currently running.");
            await _messageSink.WriteAsync(
                TurnMessageFactory.CreateFinished(AgwTurnStatus.Interrupted),
                CancellationToken.None
            );
            return;
        }

        Host!.RequestInterrupt();
    }

    public async Task SubmitHumanDecisionAsync(HumanResponseCommand command, CancellationToken cancellationToken)
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        ArgumentNullException.ThrowIfNull(command);
        if (_attachment != null)
        {
            await _attachment.RespondAsync(command, ConversationId, cancellationToken);
            return;
        }

        if (Host == null || !await Host.TrySubmitHumanResponseAsync(command.Response, cancellationToken))
        {
            await SendSystemMessageAsync("No matching interaction is waiting for this response.");
        }
    }

    public async Task<IReadOnlyList<AgentflowCheckpointAvailability>> GetAgentflowCheckpointsAsync(
        Guid agentflowId,
        CancellationToken cancellationToken
    )
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        if (agentflowId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "agentflowId is required.");
        }

        var checkpointStore =
            _checkpointStore
            ?? throw new AgwException(ErrorCodes.InvalidParam, "Agentflow checkpoint services are not configured.");
        var settings =
            Settings
            ?? throw new AgwException(
                ErrorCodes.InvalidParam,
                "Execution settings must be configured before querying checkpoints."
            );
        IReadOnlySet<Guid>? inProcessOccurrences = null;
        if (
            Host?.AgentflowRuntime is { } runtime
            && _target is { AgentType: AgentRuntimeType.Agentflow, AgentId: var targetId }
            && targetId == agentflowId
        )
        {
            inProcessOccurrences = runtime.CheckpointOccurrenceIds;
        }

        var projectId = await ResolveProjectIdAsync(settings, cancellationToken).ConfigureAwait(false);
        // 尚未保存的草稿对话、以及其他用户的对话都没有 checkpoint。
        // A draft conversation that is not saved yet, like another user's conversation, has no checkpoints.
        var contextId = await _projectTasks
            .FindContextIdAsync(projectId, RequireConversationId(settings), cancellationToken)
            .ConfigureAwait(false);
        if (contextId == null)
        {
            return [];
        }

        return await checkpointStore
            .ListAsync(projectId, contextId, agentflowId, _userId, inProcessOccurrences, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ResumeCheckpointAsync(ResumeCheckpointCommand command, CancellationToken cancellationToken)
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        ArgumentNullException.ThrowIfNull(command);
        if (
            command.CheckpointOccurrenceId == Guid.Empty
            || command.ResumeExecutionId == Guid.Empty
            || command.AgentflowId == Guid.Empty
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "checkpointOccurrenceId, resumeExecutionId and agentflowId are required."
            );
        }
        if (
            _resolvedTask != null
            && await _projectTasks.GetGenerationAsync(_resolvedTask.ProjectConversationId, cancellationToken)
                != _resolvedTask.Generation
        )
        {
            await ReleaseRuntimeAsync();
            _resolvedTask = null;
            _lastResumeExecutionId = null;
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }
        if (_lastResumeExecutionId == command.ResumeExecutionId)
        {
            return;
        }
        if (HasActiveTurn)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "Stop the active Agentflow execution before resuming a checkpoint."
            );
        }

        var checkpointStore =
            _checkpointStore
            ?? throw new AgwException(ErrorCodes.InvalidParam, "Agentflow checkpoint services are not configured.");
        var settings =
            Settings
            ?? throw new AgwException(
                ErrorCodes.InvalidParam,
                "Execution settings must be configured before resuming a checkpoint."
            );
        if (_permissions != null)
            ExecutionPermissionService.Validate(
                await _permissions.GetAsync(AgentRuntimeType.Agentflow, command.AgentflowId, cancellationToken),
                settings.PermissionMode
            );
        var projectId = await ResolveProjectIdAsync(settings, cancellationToken).ConfigureAwait(false);
        var conversationId = RequireConversationId(settings);
        var contextId =
            await _projectTasks.FindContextIdAsync(projectId, conversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "The conversation was not found.");

        if (_attachment != null)
        {
            await _attachment
                .ResumeCheckpointAsync(
                    command.CheckpointOccurrenceId,
                    command.ResumeExecutionId,
                    projectId,
                    conversationId,
                    contextId,
                    command.AgentflowId,
                    cancellationToken,
                    DurableExecutionMapper.FromSettings(settings)
                )
                .ConfigureAwait(false);
            _turnSettings = settings;
            await SendPermissionStatusAsync(starting: true);
            _target = new ExecutionTarget(command.AgentflowId, AgentRuntimeType.Agentflow);
            _lastResumeExecutionId = command.ResumeExecutionId;
            return;
        }

        if (
            Host?.AgentflowRuntime is not { } runtime
            || _target is not { AgentType: AgentRuntimeType.Agentflow, AgentId: var targetId }
            || targetId != command.AgentflowId
            || !runtime.TryGetCheckpoint(command.CheckpointOccurrenceId, out _)
        )
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The selected in-process checkpoint is no longer available."
            );
        }
        var resolvedTask =
            _resolvedTask
            ?? throw new AgwException(
                ErrorCodes.InvalidParam,
                "The execution task is not available for checkpoint resume."
            );

        var snapshot = await checkpointStore
            .PrepareInProcessResumeAsync(
                command.CheckpointOccurrenceId,
                projectId,
                contextId,
                command.AgentflowId,
                _userId,
                cancellationToken
            )
            .ConfigureAwait(false);
        runtime.RemoveCheckpointsAfter(snapshot.BoundarySequence);
        await StartTurnAsync(
                new ExecCommand(AgentRuntimeType.Agentflow, new AgwUserInput { Contents = [] })
                {
                    AgentId = command.AgentflowId,
                    ConversationId = resolvedTask.ProjectConversationId,
                    ExecutionId = command.ResumeExecutionId,
                    Stream = true,
                    ResumeCheckpoint = snapshot,
                    ResumeGeneration = resolvedTask.Generation,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        _lastResumeExecutionId = command.ResumeExecutionId;
    }

    public async ValueTask DisposeAsync()
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        if (_attachment != null)
        {
            await _attachment.DisposeAsync();
        }
        await ReleaseRuntimeAsync();
        _resolvedTask = null;
        _workspace = null;
        Settings = null;
        _pendingModeChange = null;
    }

    internal bool PrepareForDetach()
    {
        if (_attachment != null)
        {
            _attachment.PrepareForDetach();
            return false;
        }

        var hasActiveTurn = HasActiveTurn;
        if (hasActiveTurn && _waitingForHuman)
        {
            Host!.RequestInterrupt();
        }

        return hasActiveTurn;
    }

    /// <summary>
    /// 等待连接内进程执行结束；durable execution 不依赖当前连接存活，因此无需等待。
    /// </summary>
    internal Task WhenIdleAsync() => Host?.WhenIdleAsync() ?? Task.CompletedTask;

    /// <summary>
    /// 将当前连接附着到属于已配置对话的 durable execution，并从指定 cursor 继续回放消息。
    /// Attaches this connection to a durable execution of the configured conversation and continues replaying messages from the cursor.
    /// </summary>
    public async Task SubscribeExecutionAsync(Guid executionId, string? cursor, CancellationToken cancellationToken)
    {
        using var userScope = UserInfoUtil.Push(CreateUserPrincipal());
        var attachment =
            _attachment
            ?? throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Durable execution services are not configured."
            );
        var settings =
            Settings
            ?? throw new AgwException(
                ErrorCodes.InvalidParam,
                "Execution settings must be configured before subscribing to an execution."
            );
        await attachment.AttachAsync(executionId, cursor, RequireConversationId(settings), cancellationToken);
        if (attachment.PermissionStatus is { } status)
        {
            _turnSettings = settings.WithPermissionSnapshot(
                status.ActivePermissionMode,
                status.ActivePermissionVersion
            );
            Settings = settings.WithPermissionSnapshot(status.NextPermissionMode, status.NextPermissionVersion);
            await SendPermissionStatusAsync();
        }
    }

    /// <summary>
    /// 对话的 Generation 变化后释放 Runtime 并丢弃已解析的项目任务，受理时重新解析。
    /// Releases the Runtime and drops the resolved project task when the conversation generation changes; acceptance resolves it again.
    /// </summary>
    private async Task RefreshResolvedTaskAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var generation = await _projectTasks.GetGenerationAsync(conversationId, cancellationToken);
        if (_resolvedTask != null && _resolvedTask.Generation != generation)
        {
            await ReleaseRuntimeAsync();
            _resolvedTask = null;
            _workspace = null;
            _lastResumeExecutionId = null;
            if (!generation.HasValue)
            {
                throw new AgwException(ErrorCodes.ResourceNotFound);
            }
        }
    }

    private static Guid RequireConversationId(ExecutionSettings settings) =>
        settings.ConversationId
        ?? throw new AgwException(
            ErrorCodes.InvalidParam,
            "Execution settings must be configured with a conversation before this operation."
        );

    private async Task<Guid> ResolveProjectIdAsync(ExecutionSettings settings, CancellationToken cancellationToken)
    {
        if (
            settings.ProjectId != Guid.Empty
            && settings.ProjectId != ProjectDefaults.DefaultBuiltInId
            && settings.ProjectId != ProjectDefaults.A2AId
        )
        {
            return settings.ProjectId;
        }

        if (_projectDefaults != null)
        {
            var projectId =
                settings.ProjectId == ProjectDefaults.A2AId
                    ? await _projectDefaults.ResolveA2AProjectIdAsync(cancellationToken).ConfigureAwait(false)
                    : await _projectDefaults.ResolveDefaultProjectIdAsync(cancellationToken).ConfigureAwait(false);
            return projectId
                ?? throw new AgwException(ErrorCodes.ResourceNotFound, "The default project was not found.");
        }

        return ProjectDefaults.DefaultBuiltInId;
    }

    private async Task ReleaseRuntimeAsync()
    {
        if (_inProcess != null)
        {
            await _inProcess.ReleaseAsync();
        }

        _target = null;
        _waitingForHuman = false;
    }

    private Task SendSystemMessageAsync(string message) =>
        _messageSink
            .WriteAsync(CreateMessage(new AgwTextContent { Content = message }), CancellationToken.None)
            .AsTask();

    private async Task ApplyQueuedModeAsync(InProcessTurnHost host, PendingModeChange change)
    {
        if (
            !ReferenceEquals(Host, host)
            || _target is not { AgentId: var targetAgentId }
            || targetAgentId != change.AgentId
            || _pendingModeChange != change
        )
        {
            return;
        }

        try
        {
            await _inProcess!.SetModeAsync(change.Mode, CancellationToken.None);
            if (_pendingModeChange != change)
            {
                return;
            }

            _pendingModeChange = null;
            await SendModeStatusAsync(change.AgentId, change.Mode);
        }
        catch (Exception exception)
        {
            if (_pendingModeChange != change)
            {
                return;
            }

            _pendingModeChange = null;
            await SendModeFailureAsync(change.AgentId, change.Mode, exception.Message);
        }
    }

    private Task SendPermissionStatusAsync(bool starting = false) =>
        _messageSink
            .WriteAsync(
                CreateMessage(
                    new AgwTextContent { Content = "Permission settings updated." },
                    new AdditionalPropertiesDictionary
                    {
                        ["type"] = AgwMessageTypes.PermissionStatus,
                        ["activePermissionMode"] = (
                            starting || HasActiveTurn ? _turnSettings : Settings
                        )?.PermissionMode,
                        ["nextPermissionMode"] = Settings?.PermissionMode,
                        ["permissionChangePending"] =
                            !starting
                            && HasActiveTurn
                            && _turnSettings?.PermissionVersion != Settings?.PermissionVersion,
                    }
                ),
                CancellationToken.None
            )
            .AsTask();

    private Task SendModeStatusAsync(Guid agentId, string mode) =>
        _messageSink
            .WriteAsync(
                CreateMessage(
                    new AgwTextContent { Content = $"Agent mode changed to '{mode}'." },
                    new AdditionalPropertiesDictionary
                    {
                        ["type"] = AgwMessageTypes.ModeStatus,
                        ["agentId"] = agentId,
                        ["mode"] = mode,
                    }
                ),
                CancellationToken.None
            )
            .AsTask();

    private Task SendModeFailureAsync(Guid agentId, string mode, string message) =>
        _messageSink
            .WriteAsync(
                CreateMessage(
                    new AgwTextContent { Content = message },
                    new AdditionalPropertiesDictionary
                    {
                        ["type"] = AgwMessageTypes.ModeChangeFailed,
                        ["agentId"] = agentId,
                        ["mode"] = mode,
                    }
                ),
                CancellationToken.None
            )
            .AsTask();

    private static AgwMessage CreateMessage(
        AgwContent content,
        AdditionalPropertiesDictionary? additionalProperties = null
    ) =>
        new(
            Guid.CreateVersion7().ToString("D"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [content],
            additionalProperties
        );

    private sealed record PendingModeChange(Guid AgentId, string Mode);

    private ClaimsPrincipal CreateUserPrincipal() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId)], "ExecutionConnection"));
}
