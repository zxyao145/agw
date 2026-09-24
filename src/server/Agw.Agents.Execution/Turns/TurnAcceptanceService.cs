using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable;
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
    private readonly ExecutionPermissionService? _permissions;
    private readonly DurableExecutionCoordinator? _durable;

    public TurnAcceptanceService(
        IProjectTaskFacade projectTasks,
        IProjectRuntimeFacade projects,
        ITurnAcceptanceWriter writer,
        IConversationTurnStore turns,
        TurnBroadcastRegistry broadcasts,
        TimeProvider timeProvider,
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
        _permissions = permissions;
        _durable = durable;
    }

    public async Task<AcceptedTurn> AcceptAsync(TurnAcceptanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Target.AgentId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
        }
        if (request.ConversationId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.conversationId is required.");
        }

        var turnId = request.TurnId is { } requested && requested != Guid.Empty ? requested : Guid.CreateVersion7();
        var task = request.Task ?? await ResolveTaskAsync(request, cancellationToken);
        if (task.ProjectConversationId != request.ConversationId)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The resolved task does not match ExecCommand.conversationId."
            );
        }

        var project =
            await _projects.GetForCurrentUserAsync(task.ProjectId, cancellationToken)
            ?? throw new AgwException(ErrorCodes.InvalidParam, $"Project '{task.ProjectId}' was not found.");
        var workspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(
            project.Id,
            project.Workspace,
            (project.AdditionalDirectories ?? []).Select(directory => new ProjectWorkspaceDirectory(
                directory.Id,
                directory.Path
            ))
        );
        if (_permissions != null)
        {
            ExecutionPermissionService.Validate(
                await _permissions.GetAsync(request.Target.AgentType, request.Target.AgentId, cancellationToken),
                request.Settings.PermissionMode
            );
        }

        var streamingScopeId = string.IsNullOrWhiteSpace(request.Input.MessageId)
            ? turnId.ToString("D")
            : request.Input.MessageId;
        var input = NormalizeInput(request.Input);
        var envelope = new TurnEnvelope(
            turnId,
            request.ConversationId,
            request.Target.AgentId,
            request.Target.AgentType,
            streamingScopeId
        );
        var hasInput = input.Contents.Count > 0;
        var executionRequest = new ExecutionRequest(
            turnId,
            request.UserId,
            request.Target,
            task,
            request.Settings,
            input,
            request.Stream,
            workspaceSnapshot
        )
        {
            RequestedMode = request.RequestedMode,
            ResumeCheckpoint = request.ResumeCheckpoint,
            Envelope = envelope,
            InputMessageId = hasInput ? Guid.Parse(input.MessageId) : null,
        };
        var start = TurnMessageFactory.CreateStarted(envelope);
        var result = await _writer
            .AcceptAsync(
                new TurnAcceptanceWrite
                {
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
                        Input = hasInput ? CreateInputRow(input, request.Target) : null,
                    },
                    Durable = _durable?.CreateRegistration(executionRequest, start),
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return new AcceptedTurn(
            executionRequest with
            {
                Lease = result.Durable?.Lease,
            },
            TurnBroadcast.Stamp(start, turnId, 1),
            result.Created,
            result.Turn,
            result.Created ? _broadcasts.GetOrCreate(turnId, request.UserId) : _broadcasts.Find(turnId, request.UserId)
        );
    }

    /// <summary>
    /// 开始消息写出后、协调层受理前的失败：Durable 在租约保护事务中写入失败状态与错误、结束事件；进程内直接更新 Turn 行并经广播写出错误与结束消息。
    /// A failure after the start message and before coordination accepts the turn: Durable writes the failed state with the error and finish events in a lease-protected transaction; in-process updates the turn row directly and writes the error and finish messages through the broadcast.
    /// </summary>
    public async Task ReportStartFailureAsync(AcceptedTurn accepted, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(failure);
        if (_durable != null)
        {
            await _durable.FailAcceptedAsync(accepted.Request, failure, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var errorCode = TurnMessageFactory.GetErrorCode(AgwTurnStatus.Failed, failure);
        var broadcast =
            accepted.Broadcast
            ?? throw new AgwException(ErrorCodes.AgentExecutionFailed, "The accepted turn has no output.");
        try
        {
            await _turns
                .FinishAsync(
                    accepted.Request.TurnId,
                    ConversationTurnStatus.Failed,
                    0,
                    errorCode,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        finally
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
                    TurnMessageFactory.CreateFinished(accepted.Request.Envelope, AgwTurnStatus.Failed, 0, errorCode),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
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
                ContextId: request.Settings.ContextId,
                Input: AgwMessageUtil.ExtractInputText(request.Input),
                Resume: request.Settings.Resume,
                OwnerUserId: request.UserId
            ),
            cancellationToken
        );
        return ProjectTaskProjectionMapper.Map(task);
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
