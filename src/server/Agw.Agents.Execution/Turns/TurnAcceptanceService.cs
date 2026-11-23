using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Extensions.Contracts;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.History;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// 受理一个 Turn：校验目标与对话，确定 turnId，解析项目任务、工作目录快照与权限能力，再在一个事务中写入输入行与 Turn 行（Durable 还有执行记录与开始事件）。
/// 事务提交后才能写出 agw-turn-start。
/// Accepts one turn: validates the target and conversation, fixes the turnId, resolves the project task, workspace snapshot and permission capabilities, then writes the input row and turn row (plus the execution record and start event for Durable) in one transaction.
/// agw-turn-start may be written only after that transaction commits.
/// </summary>
/// <remarks>
/// 连接与 Facade 共用本服务；调用方已经建立所属用户的身份。
/// Connections and the Facade share this service; callers have already established the owner's identity.
/// </remarks>
internal sealed class TurnAcceptanceService
{
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;

    private readonly IProjectTaskFacade _projectTasks;
    private readonly IProjectRuntimeFacade _projects;
    private readonly ITurnAcceptanceWriter _writer;
    private readonly IConversationTurnStore _turns;
    private readonly TurnBroadcastRegistry _broadcasts;
    private readonly TimeProvider _timeProvider;
    private readonly IExtensionCapabilityCatalog _extensionCapabilityCatalog;
    private readonly IExtensionExecutionSelectionResolver _extensionSelectionResolver;
    private readonly ExecutionPermissionService? _permissions;
    private readonly DurableExecutionCoordinator? _durable;

    public TurnAcceptanceService(
        IProjectTaskFacade projectTasks,
        IProjectRuntimeFacade projects,
        ITurnAcceptanceWriter writer,
        IConversationTurnStore turns,
        TurnBroadcastRegistry broadcasts,
        TimeProvider timeProvider,
        IExtensionCapabilityCatalog extensionCapabilityCatalog,
        IExtensionExecutionSelectionResolver extensionSelectionResolver,
        ExecutionPermissionService? permissions = null,
        DurableExecutionCoordinator? durable = null
    )
    {
        _projectTasks = projectTasks;
        _projects = projects;
        _writer = writer;
        _turns = turns;
        _broadcasts = broadcasts;
        _timeProvider = timeProvider;
        _extensionCapabilityCatalog = extensionCapabilityCatalog;
        _extensionSelectionResolver = extensionSelectionResolver;
        _permissions = permissions;
        _durable = durable;
    }

    public async Task<AcceptedTurn> AcceptAsync(TurnAcceptanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var resolved = await ResolveAcceptanceAsync(request, cancellationToken).ConfigureAwait(false);
        return await AcceptResolvedAsync(request, resolved, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedTurnAcceptance> ResolveAcceptanceAsync(
        TurnAcceptanceRequest request,
        CancellationToken cancellationToken
    )
    {
        var turnId = request.TurnId is { } requested && requested != Guid.Empty ? requested : Guid.CreateVersion7();
        var task = request.Task ?? await ResolveTaskAsync(request, cancellationToken);
        EnsureTaskConversationMatches(request, task);

        var project =
            await _projects.GetForCurrentUserAsync(task.ProjectId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.InvalidParam, $"Project '{task.ProjectId}' was not found.");
        return new ResolvedTurnAcceptance
        {
            TurnId = turnId,
            Task = task,
            Project = project,
            WorkspaceSnapshot = CreateWorkspaceSnapshot(project),
            Input = NormalizeInput(request.Input),
        };
    }

    private async Task<AcceptedTurn> AcceptResolvedAsync(
        TurnAcceptanceRequest request,
        ResolvedTurnAcceptance resolved,
        CancellationToken cancellationToken
    )
    {
        var state = new PreparedRegistrationState();
        try
        {
            var write = CreateAcceptanceWrite(
                request,
                resolved.Task,
                resolved.TurnId,
                resolved.Input,
                (snapshot, token) => RegisterAsync(request, resolved, state, snapshot, token)
            );
            var result = await _writer.AcceptAsync(write, cancellationToken).ConfigureAwait(false);
            if (!result.Created)
            {
                await state.ReleaseAsync().ConfigureAwait(false);
                return CreateReplay(request, resolved.Task, resolved.WorkspaceSnapshot, result.Turn);
            }

            var registration = state.Prepared ?? throw new AgwException(ErrorCodes.AgentExecutionFailed);
            if (_durable != null)
                await state.ReleaseAsync().ConfigureAwait(false);
            else
                state.Detach();

            return CreateAcceptedTurn(registration, result, request.UserId, resolved.TurnId);
        }
        finally
        {
            await state.ReleaseAsync().ConfigureAwait(false);
        }
    }

    private async Task<TurnAcceptanceRegistration> RegisterAsync(
        TurnAcceptanceRequest request,
        ResolvedTurnAcceptance resolved,
        PreparedRegistrationState state,
        AgentflowRevisionSnapshot? snapshot,
        CancellationToken cancellationToken
    )
    {
        state.Prepared = await PrepareRegistrationAsync(request, resolved, snapshot, cancellationToken)
            .ConfigureAwait(false);
        return state.Prepared.Registration;
    }

    private static TurnAcceptanceWrite CreateAcceptanceWrite(
        TurnAcceptanceRequest request,
        AgentExecutionTask task,
        Guid turnId,
        AgwUserInput input,
        Func<AgentflowRevisionSnapshot?, CancellationToken, Task<TurnAcceptanceRegistration>> register
    ) =>
        new()
        {
            RequestedAgentflowRevision = request.RequestedAgentflowRevision,
            PinnedSnapshot = request.ResumeCheckpoint?.RevisionSnapshot,
            Turn = new AcceptConversationTurnRequest
            {
                TurnId = turnId,
                ProjectId = task.ProjectId,
                ContextId = task.ContextId,
                ConversationId = task.ProjectConversationId,
                Generation = task.Generation,
                TaskId = task.TaskId == Guid.Empty ? null : task.TaskId,
                TargetId = request.Target.AgentId,
                TargetType =
                    request.Target.AgentType == AgentRuntimeType.Agentflow
                        ? ConversationTurnTargetType.Agentflow
                        : ConversationTurnTargetType.Agent,
                Input = input.Contents.Count == 0 ? null : CreateInputRow(input, request.Target),
            },
            Register = register,
        };

    private AcceptedTurn CreateAcceptedTurn(
        PreparedTurnAcceptanceRegistration registration,
        TurnAcceptanceResult result,
        string userId,
        Guid turnId
    )
    {
        var request = registration.Request;
        if (_durable != null)
        {
            request = request with
            {
                Settings = request.Settings.WithExtensionCapabilities(registration.ExtensionPins, null),
            };
        }
        return new AcceptedTurn(
            request with
            {
                Lease = result.Durable?.Lease,
            },
            TurnBroadcast.Stamp(registration.Start, turnId, 1),
            true,
            result.Turn,
            _broadcasts.GetOrCreate(turnId, userId)
        );
    }

    private static void ValidateRequest(TurnAcceptanceRequest request)
    {
        if (request.Target.AgentId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        }
        if (request.ConversationId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.conversationId is required.");
        }
        if (request.RequestedAgentflowRevision != 0 && request.Target.AgentType != AgentRuntimeType.Agentflow)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "ExecCommand.agentflowRevision applies to Agentflow targets only."
            );
        }
    }

    private static void EnsureTaskConversationMatches(TurnAcceptanceRequest request, AgentExecutionTask task)
    {
        if (task.ProjectConversationId != request.ConversationId)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The resolved task does not match ExecCommand.conversationId."
            );
        }
    }

    private static ProjectWorkspaceSnapshot CreateWorkspaceSnapshot(ProjectRuntimeSnapshot project) =>
        ProjectWorkspacePaths.CreateSnapshot(
            project.Id,
            project.Workspace,
            (project.AdditionalDirectories ?? []).Select(directory => new ProjectWorkspaceDirectory(
                directory.Id,
                directory.Path
            ))
        );

    private async Task<PreparedTurnAcceptanceRegistration> PrepareRegistrationAsync(
        TurnAcceptanceRequest request,
        ResolvedTurnAcceptance resolved,
        AgentflowRevisionSnapshot? snapshot,
        CancellationToken cancellationToken
    )
    {
        await ValidatePermissionModeAsync(request, snapshot, cancellationToken).ConfigureAwait(false);
        var target = snapshot is null
            ? request.Target
            : request.Target with
            {
                AgentflowRevision = snapshot.RootRevision,
            };
        var extensionSelection = await _extensionSelectionResolver
            .ResolveAsync(target, snapshot, resolved.Project, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var extensionCapabilities = await _extensionCapabilityCatalog
            .AcquireCurrentAsync(extensionSelection, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var extensionPins = extensionCapabilities?.Pins ?? [];
            return CreatePreparedRegistration(
                request,
                resolved,
                target,
                snapshot,
                extensionCapabilities,
                extensionPins
            );
        }
        catch
        {
            if (extensionCapabilities is not null)
            {
                await extensionCapabilities.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task ValidatePermissionModeAsync(
        TurnAcceptanceRequest request,
        AgentflowRevisionSnapshot? snapshot,
        CancellationToken cancellationToken
    )
    {
        if (_permissions == null)
        {
            return;
        }

        var capabilities = await _permissions
            .GetForTargetAsync(request.Target.AgentType, request.Target.AgentId, snapshot, cancellationToken)
            .ConfigureAwait(false);
        ExecutionPermissionService.Validate(capabilities, request.Settings.PermissionMode);
    }

    private PreparedTurnAcceptanceRegistration CreatePreparedRegistration(
        TurnAcceptanceRequest request,
        ResolvedTurnAcceptance resolved,
        ExecutionTarget target,
        AgentflowRevisionSnapshot? snapshot,
        ExtensionCapabilitySnapshot? extensionCapabilities,
        IReadOnlyList<ExtensionExecutionPin> extensionPins
    )
    {
        var executionRequest = CreateExecutionRequest(
            request,
            resolved,
            target,
            snapshot,
            extensionCapabilities,
            extensionPins
        );
        var start = TurnMessageFactory.CreateStarted(executionRequest.Envelope);
        var registration = new TurnAcceptanceRegistration(
            extensionPins.Select(pin => pin.ExtensionId).Distinct().Order().ToArray(),
            _durable?.CreateRegistration(executionRequest, start)
        );
        return new PreparedTurnAcceptanceRegistration(
            executionRequest,
            start,
            extensionCapabilities,
            extensionPins,
            registration
        );
    }

    private ExecutionRequest CreateExecutionRequest(
        TurnAcceptanceRequest request,
        ResolvedTurnAcceptance resolved,
        ExecutionTarget target,
        AgentflowRevisionSnapshot? snapshot,
        ExtensionCapabilitySnapshot? extensionCapabilities,
        IReadOnlyList<ExtensionExecutionPin> extensionPins
    )
    {
        var envelope = new TurnEnvelope(
            resolved.TurnId,
            request.ConversationId,
            request.Target.AgentId,
            request.Target.AgentType,
            resolved.TurnId.ToString("D")
        );
        var settings = request.Settings.WithExtensionCapabilities(extensionPins, extensionCapabilities);
        var executionRequest = new ExecutionRequest(
            resolved.TurnId,
            request.UserId,
            target,
            resolved.Task,
            settings,
            resolved.Input,
            request.Stream,
            resolved.WorkspaceSnapshot
        )
        {
            RequestedMode = request.RequestedMode,
            ResumeCheckpoint = request.ResumeCheckpoint,
            AgentflowSnapshot = snapshot,
            ExtensionPins = extensionPins,
            Envelope = envelope,
            InputMessageId = resolved.Input.Contents.Count > 0 ? Guid.Parse(resolved.Input.MessageId) : null,
        };
        return executionRequest;
    }

    private AcceptedTurn CreateReplay(
        TurnAcceptanceRequest request,
        AgentExecutionTask task,
        ProjectWorkspaceSnapshot workspaceSnapshot,
        ConversationTurnSnapshot existing
    )
    {
        var replay = CreateReplayRequest(request, task, workspaceSnapshot, existing);
        return new AcceptedTurn(
            replay.Request,
            TurnBroadcast.Stamp(TurnMessageFactory.CreateStarted(replay.Envelope), existing.TurnId, 1),
            false,
            existing,
            _broadcasts.Find(existing.TurnId, request.UserId)
        );
    }

    private ExecutionReplayRequest CreateReplayRequest(
        TurnAcceptanceRequest request,
        AgentExecutionTask task,
        ProjectWorkspaceSnapshot workspaceSnapshot,
        ConversationTurnSnapshot existing
    )
    {
        if (existing.ConversationId != request.ConversationId)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        var target = new ExecutionTarget(
            existing.TargetId,
            existing.TargetType == ConversationTurnTargetType.Agentflow
                ? AgentRuntimeType.Agentflow
                : AgentRuntimeType.Agent,
            existing.AgentflowRevision
        );
        var snapshot =
            existing.TargetType == ConversationTurnTargetType.Agentflow
                ? AgentflowRevisionSnapshot.FromTurn(existing)
                : null;
        var envelope = new TurnEnvelope(
            existing.TurnId,
            existing.ConversationId,
            existing.TargetId,
            target.AgentType,
            existing.TurnId.ToString("D")
        );
        var replay = new ExecutionRequest(
            existing.TurnId,
            request.UserId,
            target,
            task,
            request.Settings.WithExtensionCapabilities([], null),
            NormalizeInput(request.Input),
            request.Stream,
            workspaceSnapshot
        )
        {
            Envelope = envelope,
            AgentflowSnapshot = snapshot,
            InputMessageId = existing.InputMessageId == Guid.Empty ? null : existing.InputMessageId,
        };
        return new ExecutionReplayRequest(replay, envelope);
    }

    /// <summary>
    /// 开始消息写出后、协调层受理前的失败：Durable 在租约保护事务中写入失败状态与错误、结束事件；进程内直接更新 Turn 行并经广播写出错误与结束消息。
    /// A failure after the start message and before coordination accepts the turn: Durable writes the failed state with the error and finish events in a lease-protected transaction; in-process updates the turn row directly and writes the error and finish messages through the broadcast.
    /// </summary>
    public async Task ReportStartFailureAsync(AcceptedTurn accepted, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(failure);
        await accepted.Request.Settings.DisposeExtensionCapabilityLeaseAsync().ConfigureAwait(false);
        if (_durable != null)
        {
            await _durable.FailAcceptedAsync(accepted.Request, failure, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await ReportInProcessStartFailureAsync(accepted, failure).ConfigureAwait(false);
    }

    private async Task ReportInProcessStartFailureAsync(AcceptedTurn accepted, Exception failure)
    {
        var errorCode = TurnMessageFactory.GetErrorCode(AgwTurnStatus.Failed, failure);
        var broadcast =
            accepted.Broadcast
            ?? throw new AgwException(ErrorCodes.AgentExecutionFailed, "The accepted turn has no output.");
        try
        {
            // 执行没有开始，用量从结束时间起结算。
            // Execution never started, so usage settles at the finish time.
            await _turns
                .FinishAsync(
                    accepted.Request.TurnId,
                    ConversationTurnStatus.Failed,
                    0,
                    errorCode,
                    _timeProvider.GetUtcNow(),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        finally
        {
            await WriteStartFailureMessagesAsync(broadcast, accepted.Request, failure, errorCode).ConfigureAwait(false);
        }
    }

    private static async Task WriteStartFailureMessagesAsync(
        TurnBroadcast broadcast,
        ExecutionRequest request,
        Exception failure,
        string? errorCode
    )
    {
        await broadcast
            .WriteAsync(
                new AgwMessage(
                    Guid.CreateVersion7().ToString("D"),
                    Constants.DefaultAgentAuthor,
                    AiRole.System,
                    [new AgwErrorContent { Content = failure.Message }]
                ),
                CancellationToken.None
            )
            .ConfigureAwait(false);
        await broadcast
            .WriteAsync(
                TurnMessageFactory.CreateFinished(request.Envelope, AgwTurnStatus.Failed, 0, errorCode),
                CancellationToken.None
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 同一 turnId 重发时的结局：Turn 行已结束或不在本实例运行时返回对应的结束状态。
    /// The outcome for a resent turnId: when the turn row has finished or the turn is not running on this instance, the matching finish status is returned.
    /// </summary>
    public static string ToFinishedStatus(ConversationTurnStatus status) =>
        status switch
        {
            ConversationTurnStatus.Completed => AgwTurnStatus.Completed,
            ConversationTurnStatus.Failed => AgwTurnStatus.Failed,
            _ => AgwTurnStatus.Interrupted,
        };

    /// <summary>
    /// 输入行 Id 取客户端的消息 ID；不是 GUID 时换成新 GUID。创建时间在受理时确定，输入行与执行时的输入消息使用相同的 ID 与时间。
    /// The input row Id is the client's message ID; a non-GUID ID is replaced with a new GUID. The creation time is fixed at acceptance, and the input row and the execution input message share the ID and time.
    /// </summary>
    private AgwUserInput NormalizeInput(AgwUserInput input) =>
        new()
        {
            MessageId = Guid.TryParse(input.MessageId, out _) ? input.MessageId : Guid.CreateVersion7().ToString("D"),
            CreatedAt = input.CreatedAt ?? _timeProvider.GetUtcNow(),
            Author = input.Author,
            Contents = input.Contents,
        };

    /// <summary>
    /// 用户输入行的内容与执行时的输入消息一致，并在第一段内容上记录本轮目标。
    /// The user input row matches the execution input message and records this turn's target on its first content.
    /// </summary>
    private static ConversationTurnInput CreateInputRow(AgwUserInput input, ExecutionTarget target)
    {
        var message = AgwMessageUtil
            .CreateExecutionInputMessages(input, target.AgentType, target.AgentId, ConversationHandoff.Empty)
            .Single();
        return new ConversationTurnInput(
            Guid.Parse(input.MessageId),
            input.CreatedAt!.Value,
            message.AuthorName,
            JsonSerializer.Serialize(message, JsonOptions)
        );
    }

    private async Task<AgentExecutionTask> ResolveTaskAsync(
        TurnAcceptanceRequest request,
        CancellationToken cancellationToken
    )
    {
        var task = await _projectTasks.ResolveAsync(
            new ResolveProjectTaskRequest(
                TaskId: null,
                ConversationId: request.ConversationId,
                ProjectId: request.Settings.ProjectId,
                Input: AgwMessageUtil.ExtractInputText(request.Input),
                Resume: request.Settings.Resume,
                OwnerUserId: request.UserId,
                RequesterExtensionId: request.Settings.ExtensionId
            ),
            cancellationToken
        );
        return ProjectTaskProjectionMapper.Map(task);
    }

    private sealed record PreparedTurnAcceptanceRegistration
    {
        public PreparedTurnAcceptanceRegistration(
            ExecutionRequest request,
            AgwMessage start,
            ExtensionCapabilitySnapshot? extensionCapabilities,
            IReadOnlyList<ExtensionExecutionPin> extensionPins,
            TurnAcceptanceRegistration registration
        )
        {
            Request = request;
            Start = start;
            ExtensionCapabilities = extensionCapabilities;
            ExtensionPins = extensionPins;
            Registration = registration;
        }

        public ExecutionRequest Request { get; }
        public AgwMessage Start { get; }
        public ExtensionCapabilitySnapshot? ExtensionCapabilities { get; }
        public IReadOnlyList<ExtensionExecutionPin> ExtensionPins { get; }
        public TurnAcceptanceRegistration Registration { get; }
    }

    private sealed record ResolvedTurnAcceptance
    {
        public required Guid TurnId { get; init; }
        public required AgentExecutionTask Task { get; init; }
        public required ProjectRuntimeSnapshot Project { get; init; }
        public required ProjectWorkspaceSnapshot WorkspaceSnapshot { get; init; }
        public required AgwUserInput Input { get; init; }
    }

    private sealed record ExecutionReplayRequest(ExecutionRequest Request, TurnEnvelope Envelope);

    private sealed class PreparedRegistrationState
    {
        public PreparedTurnAcceptanceRegistration? Prepared { get; set; }

        public void Detach() => Prepared = null;

        public async ValueTask ReleaseAsync()
        {
            if (Prepared?.ExtensionCapabilities is { } capabilities)
            {
                await capabilities.DisposeAsync().ConfigureAwait(false);
                Prepared = null;
            }
        }
    }
}

/// <summary>
/// 受理一个 Turn 所需的输入；Task 为空时按对话解析项目任务。
/// The input for accepting one turn; a null Task resolves the project task from the conversation.
/// </summary>
internal sealed record TurnAcceptanceRequest(
    string UserId,
    Guid? TurnId,
    ExecutionTarget Target,
    Guid ConversationId,
    AgwUserInput Input,
    ExecutionSettings Settings,
    bool Stream
)
{
    public AgentExecutionTask? Task { get; init; }

    public string? RequestedMode { get; init; }

    public int RequestedAgentflowRevision { get; init; }

    public AgentflowCheckpointSnapshot? ResumeCheckpoint { get; init; }
}

/// <summary>
/// 已经提交的受理：Start 是带 turnSequence = 1 的开始消息；Created 为假表示同一 turnId 的重发，Turn 是当前记录，
/// Broadcast 只在本实例还保留该 Turn 时存在。
/// A committed acceptance: Start is the start message with turnSequence = 1; Created is false for a resend of the same turnId, Turn is the current record,
/// and Broadcast exists only while this instance still keeps the turn.
/// </summary>
internal sealed record AcceptedTurn(
    ExecutionRequest Request,
    AgwMessage Start,
    bool Created,
    ConversationTurnSnapshot Turn,
    TurnBroadcast? Broadcast
);
