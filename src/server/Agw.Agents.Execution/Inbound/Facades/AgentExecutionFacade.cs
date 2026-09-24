using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading.Channels;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Inbound.Facades;

/// <summary>
/// A2A 与 Jobs 的执行入口：与 SignalR 连接走同一条路径，经 TurnAcceptanceService 受理后交给协调器，并从消息流取结果。
/// The execution entry of A2A and Jobs: it follows the same path as SignalR connections, handing the turn to a coordinator after TurnAcceptanceService accepts it and reading the result from the message stream.
/// </summary>
internal sealed class AgentExecutionFacade : IAgentExecutionFacade, IDurableAgentExecutionFacade
{
    private const string HumanInteractionUnsupportedReason =
        "This unattended execution does not support human interaction.";

    private readonly TurnAcceptanceService _acceptance;
    private readonly IAgentCatalogFacade _catalog;
    private readonly InProcessExecutionCoordinatorFactory? _inProcessCoordinators;
    private readonly DurableExecutionCoordinator? _durableCoordinator;

    public AgentExecutionFacade(
        TurnAcceptanceService acceptance,
        IAgentCatalogFacade catalog,
        InProcessExecutionCoordinatorFactory? inProcessCoordinators = null,
        DurableExecutionCoordinator? durableCoordinator = null
    )
    {
        _acceptance = acceptance;
        _catalog = catalog;
        _inProcessCoordinators = inProcessCoordinators;
        _durableCoordinator = durableCoordinator;
    }

    /// <summary>
    /// 执行到终态并返回消息流中的全部消息；Durable 执行进入人工等待且允许交互时返回 WaitingForHuman。
    /// Runs to a terminal state and returns every message of the stream; a durable execution that waits for a human with interaction allowed returns WaitingForHuman.
    /// </summary>
    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = new List<AgwMessage>();
        await foreach (var message in RunTurnAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (AgwMessageClassifier.TryGetTurnFinishedStatus(message, out var status))
            {
                if (status is AgwTurnStatus.Failed or AgwTurnStatus.Interrupted)
                {
                    throw new AgwException(ErrorCodes.AgentExecutionFailed, FindErrorText(messages));
                }
                return new AgentExecutionResult(request.ExecutionId, AgentExecutionState.Completed, messages);
            }
            messages.Add(message);
            if (IsHumanInteraction(message))
            {
                return new AgentExecutionResult(request.ExecutionId, AgentExecutionState.WaitingForHuman, messages);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new AgwException(ErrorCodes.AgentExecutionFailed, "The execution ended without a terminal message.");
    }

    public async IAsyncEnumerable<AgentExecutionEvent> ExecuteStreamingAsync(
        AgentExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var messages = new List<AgwMessage>();
        await foreach (var message in RunTurnAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return new AgentExecutionEvent(null, message);
            if (AgwMessageClassifier.TryGetTurnFinishedStatus(message, out var status))
            {
                if (status == AgwTurnStatus.Failed)
                {
                    throw new AgwException(ErrorCodes.AgentExecutionFailed, FindErrorText(messages));
                }
                yield break;
            }
            messages.Add(message);
        }
    }

    public async Task<AgentExecutionResult> GetOutcomeAsync(
        Guid executionId,
        string ownerUserId,
        CancellationToken cancellationToken = default
    )
    {
        using var userScope = PushExecutionUser(ownerUserId);
        var outcome = await DurableCoordinator
            .GetOutcomeAsync(executionId, ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        return new AgentExecutionResult(outcome.ExecutionId, Map(outcome.Status), [], outcome.ErrorMessage);
    }

    public async IAsyncEnumerable<AgentExecutionEvent> SubscribeAsync(
        Guid executionId,
        string ownerUserId,
        string? afterCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        using var userScope = PushExecutionUser(ownerUserId);
        await foreach (
            var entry in DurableCoordinator
                .ReadAsync(
                    executionId,
                    ownerUserId,
                    DurableExecutionAttachment.ParseCursor(afterCursor),
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            yield return new AgentExecutionEvent(
                entry.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.Message
            );
        }
    }

    public async Task<bool> InterruptAsync(
        Guid executionId,
        string ownerUserId,
        string reason,
        CancellationToken cancellationToken = default
    )
    {
        using var userScope = PushExecutionUser(ownerUserId);
        return await DurableCoordinator
            .InterruptAsync(executionId, ownerUserId, reason, cancellationToken)
            .ConfigureAwait(false);
    }

    private DurableExecutionCoordinator DurableCoordinator =>
        _durableCoordinator ?? throw new AgwException(ErrorCodes.DurableExecutionUnavailable);

    /// <summary>
    /// 受理并启动 Turn，返回除开始消息之外的消息流；不允许人工交互的执行遇到交互请求时中断并报错。
    /// Accepts and starts the turn and returns the message stream without the start message; an execution that rejects human interaction is interrupted when a request appears.
    /// </summary>
    private async IAsyncEnumerable<AgwMessage> RunTurnAsync(
        AgentExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var userScope = PushExecutionUser(request.OwnerUserId);
        var target = await ResolveTargetAsync(request.Target, cancellationToken).ConfigureAwait(false);
        var task = ProjectTaskProjectionMapper.Map(request.Task);
        // 进程内 Facade 调用没有回答通道，人工请求一律按无人值守规则处理。
        // An in-process Facade call has no answer channel, so every human request follows the unattended rules.
        var settings = new ExecutionSettings(
            task.ProjectId,
            task.ContextId,
            permissionMode: MapPermissionMode(request.PermissionMode),
            resume: request.Resume
        ).WithHumanInteractionPolicy(
            _durableCoordinator == null ? HumanInteractionPolicy.Reject : request.HumanInteractionPolicy
        );
        var accepted = await _acceptance
            .AcceptAsync(
                new TurnAcceptanceRequest(
                    UserInfoUtil.RequiredUserId,
                    request.ExecutionId,
                    target,
                    task.ProjectConversationId,
                    request.Input,
                    settings,
                    Stream: true
                )
                {
                    Task = task,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        await foreach (var message in StartAndReadAsync(accepted, cancellationToken).ConfigureAwait(false))
        {
            if (AgwMessageClassifier.IsTurnStart(message))
            {
                continue;
            }
            if (request.HumanInteractionPolicy == HumanInteractionPolicy.Reject && IsHumanInteraction(message))
            {
                if (_durableCoordinator != null)
                {
                    await _durableCoordinator
                        .InterruptAsync(
                            accepted.Request.TurnId,
                            accepted.Request.UserId,
                            HumanInteractionUnsupportedReason,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }
                throw new AgwException(ErrorCodes.AgentExecutionFailed, "Human interaction is not supported.");
            }
            yield return message;
        }
    }

    /// <summary>
    /// 启动已受理的 Turn 并读取它的消息：Durable 从已提交事件读取开始消息之后的部分；进程内订阅本 Turn 的广播。
    /// 协调层受理前的失败按 Turn 协议写出错误与结束消息。
    /// Starts the accepted turn and reads its messages: Durable reads committed events after the start message; in-process subscribes to the turn's broadcast.
    /// A failure before coordination accepts the turn writes the error and finish messages under the turn protocol.
    /// </summary>
    private async IAsyncEnumerable<AgwMessage> StartAndReadAsync(
        AcceptedTurn accepted,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var request = accepted.Request;
        if (_durableCoordinator != null)
        {
            await StartOrReportAsync(_durableCoordinator, accepted, cancellationToken).ConfigureAwait(false);
            await foreach (
                var entry in _durableCoordinator
                    .ReadAsync(request.TurnId, request.UserId, afterSequence: 1, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return entry.Message;
            }
            yield break;
        }

        var coordinators =
            _inProcessCoordinators
            ?? throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "In-process execution services are not configured."
            );
        var output = new ChannelMessageSink();
        var broadcast =
            accepted.Broadcast
            ?? throw new AgwException(ErrorCodes.AgentExecutionFailed, "The accepted turn has no output.");
        broadcast.AddSink(output);
        await broadcast.WriteAsync(accepted.Start, CancellationToken.None).ConfigureAwait(false);
        await using var coordinator = coordinators.Create(cancellationToken);
        await StartOrReportAsync(coordinator, accepted, cancellationToken).ConfigureAwait(false);
        await foreach (var message in output.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
            if (AgwMessageClassifier.IsTurnFinished(message))
            {
                yield break;
            }
        }
    }

    private async Task StartOrReportAsync(
        IExecutionCoordinator coordinator,
        AcceptedTurn accepted,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var receipt = await coordinator.StartAsync(accepted.Request, cancellationToken).ConfigureAwait(false);
            if (!receipt.Accepted)
            {
                throw new AgwException(ErrorCodes.UnableToCreateAgentSession);
            }
        }
        catch (Exception exception)
        {
            await _acceptance.ReportStartFailureAsync(accepted, exception).ConfigureAwait(false);
        }
    }

    private async Task<ExecutionTarget> ResolveTargetAsync(AgentTarget target, CancellationToken cancellationToken)
    {
        if (target.Id is { } id && id != Guid.Empty)
        {
            var runtimeType =
                target.Kind == AgentTargetKind.Agent ? AgentRuntimeType.Agent : AgentRuntimeType.Agentflow;
            if (!await _catalog.IsOwnedTargetAsync(runtimeType, id, UserInfoUtil.RequiredUserId, cancellationToken))
            {
                throw new AgwException(ErrorCodes.ResourceNotFound);
            }

            return new ExecutionTarget(id, runtimeType);
        }
        if (target.Kind != AgentTargetKind.Agent || string.IsNullOrWhiteSpace(target.Name))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The Agent execution target is invalid.");
        }

        var descriptor = await _catalog
            .FindDiscoverableByNameAsync(target.Name, cancellationToken)
            .ConfigureAwait(false);
        return descriptor == null
            ? throw new AgwException(ErrorCodes.AgentNotFound, $"Agent '{target.Name}' was not found.")
            : new ExecutionTarget(descriptor.Id, AgentRuntimeType.Agent);
    }

    private static bool IsHumanInteraction(AgwMessage message) => AgwMessageClassifier.IsInteractionRequest(message);

    private static string FindErrorText(IReadOnlyList<AgwMessage> messages) =>
        messages
            .SelectMany(message => message.Contents)
            .OfType<AgwErrorContent>()
            .Select(content => content.Content)
            .LastOrDefault(content => !string.IsNullOrWhiteSpace(content))
        ?? "Agent execution failed.";

    private static AgwPermissionMode? MapPermissionMode(AgentExecutionPermissionMode? mode) =>
        mode switch
        {
            null => null,
            AgentExecutionPermissionMode.FullAccess => AgwPermissionMode.FullAccess,
            AgentExecutionPermissionMode.AlwaysAsk => AgwPermissionMode.AlwaysAsk,
            AgentExecutionPermissionMode.AllowSameArguments => AgwPermissionMode.AllowSameArguments,
            _ => throw new AgwException(ErrorCodes.InvalidParam, "Unsupported execution permission mode."),
        };

    private static AgentExecutionState Map(DurableExecutionStatus status) =>
        status switch
        {
            DurableExecutionStatus.Queued => AgentExecutionState.Queued,
            DurableExecutionStatus.Running or DurableExecutionStatus.Resuming => AgentExecutionState.Running,
            DurableExecutionStatus.WaitingForHuman => AgentExecutionState.WaitingForHuman,
            DurableExecutionStatus.Completed => AgentExecutionState.Completed,
            DurableExecutionStatus.Failed => AgentExecutionState.Failed,
            DurableExecutionStatus.Interrupted => AgentExecutionState.Interrupted,
            _ => throw new AgwException(ErrorCodes.InvalidParam, $"Unsupported execution status '{status}'."),
        };

    private static ClaimsPrincipal CreateUserPrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "AgentExecutionFacade"));

    private static IDisposable PushExecutionUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

        var normalizedUserId = userId.Trim();
        if (
            UserInfoUtil.IsContextActive
            && !string.Equals(UserInfoUtil.RequiredUserId, normalizedUserId, StringComparison.Ordinal)
        )
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        return UserInfoUtil.Push(CreateUserPrincipal(normalizedUserId));
    }

    /// <summary>
    /// 把进程内 Turn 的消息交给 Facade 读取。
    /// Hands the messages of an in-process turn to the Facade reader.
    /// </summary>
    private sealed class ChannelMessageSink : IExecutionMessageSink
    {
        private readonly Channel<AgwMessage> _messages = Channel.CreateUnbounded<AgwMessage>(
            new UnboundedChannelOptions { SingleReader = true }
        );

        public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) =>
            _messages.Writer.WriteAsync(message, cancellationToken);

        public IAsyncEnumerable<AgwMessage> ReadAllAsync(CancellationToken cancellationToken) =>
            _messages.Reader.ReadAllAsync(cancellationToken);
    }
}
