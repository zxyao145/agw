using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands.Checkpoint;
using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Mapping;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Turns;
using Agw.Files.Utils;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Connections;

public sealed class ExecutionConnectionContext : IAsyncDisposable
{
    private const string SetModeAfterTurnActionKey = "set-mode";
    internal const string BusyMessage =
        "The previous execution is currently in progress, please wait and execute again.";

    private readonly string _userId;
    private readonly IExecutionMessageSink _messageSink;
    private readonly CancellationToken _hostToken;
    private readonly IRuntimeFactory _runtimeFactory;
    private readonly IProjectTaskFacade _projectTasks;
    private readonly IProjectRuntimeFacade _projects;
    private readonly DurableExecutionSession? _durableSession;
    private readonly AgentflowCheckpointStore? _checkpointStore;
    private RuntimeBase? _runtime;
    private TaskProjection? _resolvedTask;
    private string? _workspace;
    private ExecutionTarget? _target;
    private PendingModeChange? _pendingModeChange;
    private Guid? _lastResumeExecutionId;
    private volatile bool _waitingForHuman;

    internal ExecutionConnectionContext(
        string userId,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken,
        IRuntimeFactory runtimeFactory,
        IProjectTaskFacade projectTasks,
        IProjectRuntimeFacade projects,
        DurableExecutionSession? durableSession = null,
        AgentflowCheckpointStore? checkpointStore = null
    )
    {
        _userId = string.IsNullOrWhiteSpace(userId) ? Constants.AdminUserId : userId.Trim();
        _messageSink = messageSink;
        _hostToken = hostToken;
        _runtimeFactory = runtimeFactory;
        _projectTasks = projectTasks;
        _projects = projects;
        _durableSession = durableSession;
        _checkpointStore = checkpointStore;
    }

    public ExecutionSettings? Settings { get; private set; }

    public string UserId => _userId;

    public Guid? ProjectId => _resolvedTask?.ProjectId ?? Settings?.ProjectId;

    public Guid? ProjectConversationId => _resolvedTask?.ProjectConversationId;

    public string? ContextId => _resolvedTask?.ContextId ?? Settings?.ContextId;

    public TaskProjection? ResolvedTask => _resolvedTask;

    public string? Workspace => _workspace;

    public ExecutionTarget? Target => _target;

    public Guid? AgentId => _target?.AgentId;

    public AgentRuntimeType? AgentType => _target?.AgentType;

    public bool HasActiveTurn
    {
        get
        {
            if (_runtime is { HasActiveTurn: true })
            {
                return true;
            }

            return _durableSession?.HasActiveExecution == true;
        }
    }

    public async Task ApplySettingsAsync(ExecutionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (HasActiveTurn)
        {
            await SendErrorAsync(BusyMessage);
            return;
        }

        if (settings.Equals(Settings))
        {
            return;
        }

        await ReleaseRuntimeAsync();
        Settings = settings;
        _resolvedTask = null;
        _workspace = null;
        _target = null;
        _lastResumeExecutionId = null;
    }

    public async Task StartTurnAsync(ExecCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (HasActiveTurn)
        {
            if (
                _durableSession != null
                && command.ExecutionId.HasValue
                && command.ExecutionId == _durableSession.ActiveExecutionId
            )
            {
                await SubscribeExecutionAsync(command.ExecutionId.Value, cursor: null, cancellationToken);
                return;
            }

            await SendErrorAsync(BusyMessage);
            return;
        }

        if (_runtime != null)
        {
            await _runtime.WhenIdleAsync();
        }

        var agentId =
            command.AgentId ?? throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        if (agentId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        }
        command.ExecutionId ??= Guid.CreateVersion7();

        Settings ??= ExecutionSettings.CreateDefault();
        await ResolveExecutionContextAsync(command, cancellationToken);

        var target = new ExecutionTarget(agentId, command.AgentType);
        if (_durableSession != null)
        {
            await _durableSession.StartAsync(command, _resolvedTask!, Settings, cancellationToken);
            _target = target;
            return;
        }

        if (_target.HasValue && _target.Value != target)
        {
            await ReleaseRuntimeAsync();
        }

        var turnContext = new RuntimeTurnContext(
            Settings,
            _resolvedTask!,
            target,
            _workspace!,
            _messageSink,
            pending => _waitingForHuman = pending != null
        )
        {
            UserId = _userId,
        };
        var requestedMode =
            command.AgentType == AgentRuntimeType.Agent
            && _pendingModeChange is { } pendingModeChange
            && pendingModeChange.AgentId == agentId
                ? pendingModeChange.Mode
                : null;
        var start = await _runtimeFactory.StartAsync(
            new RuntimeStartRequest(target.AgentId, _resolvedTask!, command, _runtime, turnContext)
            {
                RequestedMode = requestedMode,
            },
            _hostToken
        );
        _runtime = start.Runtime;
        _target = start.Runtime == null ? null : target;
        if (requestedMode != null && start.Runtime != null)
        {
            _pendingModeChange = null;
            await SendModeStatusAsync(agentId, requestedMode);
        }

        if (start.ActiveTurn == null)
        {
            await SendErrorAsync("Agent execution could not be started.");
        }
    }

    public async Task SetModeAsync(Guid agentId, string mode, CancellationToken cancellationToken)
    {
        var change = new PendingModeChange(agentId, mode);
        _pendingModeChange = change;
        if (
            _runtime is not AgentRuntime runtime
            || _target is not { AgentId: var targetAgentId }
            || targetAgentId != agentId
        )
        {
            return;
        }

        if (runtime.TryScheduleAfterTurn(SetModeAfterTurnActionKey, _ => ApplyQueuedModeAsync(runtime, change)))
        {
            return;
        }

        await _runtimeFactory.SetModeAsync(runtime, mode, cancellationToken);
        if (_pendingModeChange == change)
        {
            _pendingModeChange = null;
        }

        await SendModeStatusAsync(agentId, mode);
    }

    public async Task SetPermissionModeAsync(PermissionMode permissionMode, CancellationToken cancellationToken)
    {
        Settings = (Settings ?? ExecutionSettings.CreateDefault()).WithPermissionMode(permissionMode);
        var runtime = _runtime;
        if (runtime == null)
        {
            return;
        }

        await runtime.TrySetActivePermissionModeAsync(permissionMode, cancellationToken);
        await _runtimeFactory.SetPermissionModeAsync(runtime, permissionMode, cancellationToken);
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
        if (_durableSession != null)
        {
            await _durableSession.InterruptAsync(executionId, reason, cancellationToken);
            return;
        }

        if (!HasActiveTurn)
        {
            await SendSystemMessageAsync(reason ?? "No active request is currently running.");
            await _messageSink.WriteAsync(TurnMessageFactory.CreateFinished("interrupted"), CancellationToken.None);
            return;
        }

        _runtime!.RequestInterrupt();
    }

    public async Task SubmitHumanDecisionAsync(HumanResponseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_durableSession != null)
        {
            await _durableSession.RespondAsync(command, cancellationToken);
            return;
        }

        if (_runtime == null || !await _runtime.TrySubmitHumanResponseAsync(command, cancellationToken))
        {
            await SendSystemMessageAsync("No matching HumanGate request is waiting for this response.");
        }
    }

    public async Task<IReadOnlyList<AgentflowCheckpointAvailability>> GetAgentflowCheckpointsAsync(
        Guid agentflowId,
        CancellationToken cancellationToken
    )
    {
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
            _durableSession == null
            && _runtime is AgentflowRuntime runtime
            && _target is { AgentType: AgentRuntimeType.Agentflow, AgentId: var targetId }
            && targetId == agentflowId
        )
        {
            inProcessOccurrences = runtime.CheckpointOccurrenceIds;
        }

        return await checkpointStore
            .ListAsync(
                ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId),
                ContextIdUtil.ResolveContextId(settings.ContextId),
                agentflowId,
                _userId,
                inProcessOccurrences,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task ResumeCheckpointAsync(ResumeCheckpointCommand command, CancellationToken cancellationToken)
    {
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
        var projectId = ProjectDefaults.GetDefaultProjectIdentifier(settings.ProjectId);
        var contextId = ContextIdUtil.ResolveContextId(settings.ContextId);

        if (_durableSession != null)
        {
            await _durableSession
                .ResumeCheckpointAsync(
                    command.CheckpointOccurrenceId,
                    command.ResumeExecutionId,
                    projectId,
                    contextId,
                    command.AgentflowId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            _target = new ExecutionTarget(command.AgentflowId, AgentRuntimeType.Agentflow);
            _lastResumeExecutionId = command.ResumeExecutionId;
            return;
        }

        if (
            _runtime is not AgentflowRuntime runtime
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
                    ExecutionId = command.ResumeExecutionId,
                    Stream = true,
                    ResumeCheckpoint = snapshot,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        _lastResumeExecutionId = command.ResumeExecutionId;
    }

    public async ValueTask DisposeAsync()
    {
        if (_durableSession != null)
        {
            await _durableSession.DisposeAsync();
        }
        await ReleaseRuntimeAsync();
        _resolvedTask = null;
        _workspace = null;
        Settings = null;
        _pendingModeChange = null;
    }

    internal bool PrepareForDetach()
    {
        if (_durableSession != null)
        {
            _durableSession.PrepareForDetach();
            return false;
        }

        var hasActiveTurn = HasActiveTurn;
        if (hasActiveTurn && _waitingForHuman)
        {
            _runtime!.RequestInterrupt();
        }

        return hasActiveTurn;
    }

    /// <summary>
    /// 等待连接内进程执行结束；durable execution 不依赖当前连接存活，因此无需等待。
    /// </summary>
    internal Task WhenIdleAsync() =>
        _durableSession != null ? Task.CompletedTask : _runtime?.WhenIdleAsync() ?? Task.CompletedTask;

    /// <summary>
    /// 将当前连接附着到已有 durable execution，并从指定 cursor 继续回放消息。
    /// </summary>
    public async Task SubscribeExecutionAsync(Guid executionId, string? cursor, CancellationToken cancellationToken)
    {
        var session =
            _durableSession
            ?? throw new AgwException(
                ErrorCodes.DurableExecutionUnavailable,
                "Durable execution services are not configured."
            );
        await session.AttachAsync(executionId, cursor, cancellationToken);
    }

    private async Task ResolveExecutionContextAsync(ExecCommand command, CancellationToken cancellationToken)
    {
        if (_resolvedTask == null)
        {
            var task = await _projectTasks.ResolveAsync(
                new ResolveProjectTaskRequest(
                    TaskId: null,
                    ProjectId: Settings!.ProjectId,
                    ContextId: Settings.ContextId,
                    Input: AgwMessageUtil.ExtractInputText(command.Input),
                    Resume: Settings.Resume,
                    OwnerUserId: _userId
                ),
                cancellationToken
            );
            _resolvedTask = ProjectTaskProjectionMapper.Map(task);
        }

        if (_workspace != null)
        {
            return;
        }

        var project =
            await _projects.GetForCurrentUserAsync(_resolvedTask.ProjectId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.InvalidParam, $"Project '{_resolvedTask.ProjectId}' was not found.");
        var configuredWorkspace = string.IsNullOrEmpty(project.Workspace) ? "~/.agw/temp" : project.Workspace;
        _workspace = Path.GetFullPath(PathUtil.ExpandTilde(configuredWorkspace.Trim()));
    }

    private async Task ReleaseRuntimeAsync()
    {
        if (_runtime != null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }

        _target = null;
        _waitingForHuman = false;
    }

    private Task SendErrorAsync(string message) =>
        _messageSink
            .WriteAsync(CreateMessage(new AgwErrorContent { Content = message }), CancellationToken.None)
            .AsTask();

    private Task SendSystemMessageAsync(string message) =>
        _messageSink
            .WriteAsync(CreateMessage(new AgwTextContent { Content = message }), CancellationToken.None)
            .AsTask();

    private async Task ApplyQueuedModeAsync(AgentRuntime runtime, PendingModeChange change)
    {
        if (
            !ReferenceEquals(_runtime, runtime)
            || _target is not { AgentId: var targetAgentId }
            || targetAgentId != change.AgentId
            || _pendingModeChange != change
        )
        {
            return;
        }

        try
        {
            await _runtimeFactory.SetModeAsync(runtime, change.Mode, CancellationToken.None);
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

    private Task SendModeStatusAsync(Guid agentId, string mode) =>
        _messageSink
            .WriteAsync(
                CreateMessage(
                    new AgwTextContent { Content = $"Agent mode changed to '{mode}'." },
                    new AdditionalPropertiesDictionary
                    {
                        ["type"] = "mode-status",
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
                        ["type"] = "mode-change-failed",
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
}
