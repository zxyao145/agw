using System.Globalization;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using static Agw.Agents.Application.Persistence.DurableExecutionQueries;

namespace Agw.Agents.Execution.Runtimes.Durable;

/// <summary>
/// 一条 SignalR connection 对 durable execution 的订阅关系：从 turnSequence 游标之后读取已提交的事件。
/// 断开连接只停止订阅，不拥有也不终止 execution。
/// One SignalR connection's subscription to a durable execution: reads committed events after a turnSequence cursor.
/// Disconnecting only stops the subscription; it neither owns nor ends the execution.
/// </summary>
internal sealed class DurableExecutionAttachment : IAsyncDisposable
{
    private readonly string _userId;
    private readonly IExecutionMessageSink _messageSink;
    private readonly CancellationToken _hostToken;
    private readonly DurableExecutionCoordinator _coordinator;
    private readonly Lock _stateLock = new();
    private Guid? _activeExecutionId;
    private CancellationTokenSource? _subscriptionCts;
    private Task _subscriptionTask = Task.CompletedTask;

    public DurableExecutionAttachment(
        string userId,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken,
        DurableExecutionCoordinator coordinator
    )
    {
        _userId = string.IsNullOrWhiteSpace(userId)
            ? throw new AgwException(ErrorCodes.AuthenticationRequired)
            : userId.Trim();
        _messageSink = messageSink;
        _hostToken = hostToken;
        _coordinator = coordinator;
    }

    public bool HasActiveExecution => ActiveExecutionId.HasValue;

    public Guid? ActiveExecutionId
    {
        get
        {
            lock (_stateLock)
            {
                return _activeExecutionId;
            }
        }
    }

    internal DurableExecutionStatusResponse? PermissionStatus { get; private set; }

    /// <summary>
    /// 鉴权并确认 execution 属于该对话后附着，从游标之后继续读取事件；游标是客户端已经收到的 turnSequence，为空时从头回放。
    /// Attaches the execution after authorization and after confirming it belongs to the conversation, then continues reading events after the cursor; the cursor is the turnSequence the client already received, and an empty one replays from the start.
    /// </summary>
    public async Task AttachAsync(
        Guid executionId,
        string? cursor,
        Guid conversationId,
        CancellationToken cancellationToken
    )
    {
        if (executionId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "executionId is required.");
        }

        var afterSequence = ParseCursor(cursor);
        var status = await GetConversationStatusAsync(executionId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        await StopSubscriptionAsync().ConfigureAwait(false);
        PermissionStatus = status;
        SetActiveExecution(IsTerminal(status.Status) ? null : executionId);
        var subscriptionCts = CancellationTokenSource.CreateLinkedTokenSource(_hostToken);
        _subscriptionCts = subscriptionCts;
        _subscriptionTask = PumpAsync(executionId, afterSequence, subscriptionCts.Token);
    }

    /// <summary>
    /// 终止显式指定或当前附着的 execution；显式指定未附着的 execution 时必须属于该对话。结束事件与 Interrupted 一起提交，由订阅送达。
    /// Interrupts the explicit or attached execution; an explicit execution that is not attached must belong to the conversation. The finish event commits with Interrupted and the subscription delivers it.
    /// </summary>
    public async Task InterruptAsync(
        Guid? executionId,
        string? reason,
        Guid? conversationId,
        CancellationToken cancellationToken
    )
    {
        var targetExecutionId = executionId ?? ActiveExecutionId;
        if (!targetExecutionId.HasValue)
        {
            await SendSystemMessageAsync(reason ?? "No active request is currently running.").ConfigureAwait(false);
            await _messageSink
                .WriteAsync(TurnMessageFactory.CreateFinished(AgwTurnStatus.Interrupted), CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var subscribed = ActiveExecutionId == targetExecutionId;
        if (!subscribed)
        {
            await GetConversationStatusAsync(targetExecutionId.Value, conversationId, cancellationToken)
                .ConfigureAwait(false);
        }
        var interrupted = await _coordinator
            .InterruptAsync(targetExecutionId.Value, _userId, reason, cancellationToken)
            .ConfigureAwait(false);
        if (interrupted && subscribed)
            return;
        var status = await _coordinator
            .GetStatusAsync(targetExecutionId.Value, _userId, cancellationToken)
            .ConfigureAwait(false);
        PermissionStatus = status;
        if (subscribed)
            return;
        // 没有订阅这个 execution 的连接直接收到结局，不回放整个 Turn。
        // A connection not subscribed to this execution receives the outcome directly without replaying the whole turn.
        await _messageSink
            .WriteAsync(
                TurnMessageFactory.CreateFinished(
                    status.Status switch
                    {
                        DurableExecutionStatus.Failed => AgwTurnStatus.Failed,
                        DurableExecutionStatus.Completed => AgwTurnStatus.Completed,
                        _ => AgwTurnStatus.Interrupted,
                    }
                ),
                CancellationToken.None
            )
            .ConfigureAwait(false);
    }

    public Task SetPermissionModeAsync(AgwPermissionMode mode, CancellationToken cancellationToken) =>
        ActiveExecutionId is { } executionId
            ? _coordinator.SetPermissionModeAsync(executionId, _userId, mode, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// 提交类型化响应；显式指定未附着的 execution 时必须属于该对话。请求发布统一由已提交的事件负责。
    /// Submits a typed response; an explicit execution that is not attached must belong to the conversation. Requests are published by committed events.
    /// </summary>
    public async Task RespondAsync(
        HumanResponseCommand command,
        Guid? conversationId,
        CancellationToken cancellationToken
    )
    {
        var executionId = command.ExecutionId ?? ActiveExecutionId;
        if (!executionId.HasValue)
        {
            await SendSystemMessageAsync("No matching durable human interaction is waiting for this response.")
                .ConfigureAwait(false);
            return;
        }
        if (executionId != ActiveExecutionId)
        {
            await GetConversationStatusAsync(executionId.Value, conversationId, cancellationToken)
                .ConfigureAwait(false);
        }

        await _coordinator
            .SubmitHumanResponseAsync(
                new SubmitDurableHumanResponseRequest(executionId.Value, command.Response),
                _userId,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 原子创建 checkpoint 恢复分支，并将当前 connection 附着到新 execution。
    /// Atomically creates a checkpoint resume branch and attaches this connection to the new execution.
    /// </summary>
    public async Task ResumeCheckpointAsync(
        Guid occurrenceId,
        Guid resumeExecutionId,
        Guid projectId,
        Guid conversationId,
        string contextId,
        Guid agentflowId,
        CancellationToken cancellationToken,
        DurableExecutionSettings? permissionSettings = null
    )
    {
        await _coordinator
            .ResumeCheckpointAsync(
                occurrenceId,
                resumeExecutionId,
                projectId,
                contextId,
                agentflowId,
                _userId,
                cancellationToken,
                permissionSettings
            )
            .ConfigureAwait(false);
        await AttachAsync(resumeExecutionId, cursor: null, conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 连接断开时只取消消息订阅；执行仍由持有租约的实例托管。
    /// Disconnecting only cancels the subscription; the lease-holding instance keeps running the execution.
    /// </summary>
    public void PrepareForDetach() => _subscriptionCts?.Cancel();

    public async ValueTask DisposeAsync()
    {
        await StopSubscriptionAsync().ConfigureAwait(false);
        SetActiveExecution(null);
    }

    private async Task PumpAsync(Guid executionId, long afterSequence, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (
                var entry in _coordinator
                    .ReadAsync(executionId, _userId, afterSequence, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                var message = entry.Message;
                // 回放可能发生在会话已有更新的 Turn 之后；标记后客户端不再用它更新会话状态。
                // A replay can happen after the conversation has a newer turn; the mark stops clients from updating conversation status with it.
                if (
                    (AgwMessageClassifier.IsTurnStart(message) || AgwMessageClassifier.IsTurnFinished(message))
                    && await _coordinator
                        .IsSupersededAsync(executionId, _userId, cancellationToken)
                        .ConfigureAwait(false)
                )
                {
                    message = TurnMessageFactory.MarkSuperseded(message);
                }
                // 先清除活动执行再写出结束消息：客户端收到结束消息后立即发来的下一轮不会被判为 busy。
                // The active execution clears before the finish message goes out, so the next turn sent right after the client receives it is not treated as busy.
                var finished = AgwMessageClassifier.IsTurnFinished(entry.Message);
                if (finished && ActiveExecutionId == executionId)
                {
                    SetActiveExecution(null);
                }
                await _messageSink.WriteAsync(message, cancellationToken).ConfigureAwait(false);
                if (finished)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await SendErrorAsync(exception.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 原子替换并等待旧订阅退出，避免同一 connection 同时运行两个 pump。
    /// Atomically replaces and awaits the old subscription so one connection never runs two pumps.
    /// </summary>
    private async Task StopSubscriptionAsync()
    {
        var subscriptionCts = Interlocked.Exchange(ref _subscriptionCts, null);
        var subscriptionTask = _subscriptionTask;
        _subscriptionTask = Task.CompletedTask;
        if (subscriptionCts == null)
        {
            return;
        }

        try
        {
            await subscriptionCts.CancelAsync().ConfigureAwait(false);
            await subscriptionTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            subscriptionCts.Dispose();
        }
    }

    /// <summary>
    /// 读取 execution 状态并确认它属于连接配置的对话；属于其他对话的 execution 与不存在的一样。
    /// Reads the execution status and confirms it belongs to the connection's configured conversation; an execution of another conversation looks like a missing one.
    /// </summary>
    private async Task<DurableExecutionStatusResponse> GetConversationStatusAsync(
        Guid executionId,
        Guid? conversationId,
        CancellationToken cancellationToken
    )
    {
        var expectedConversationId =
            conversationId
            ?? throw new AgwException(
                ErrorCodes.InvalidParam,
                "Execution settings must be configured before accessing an execution."
            );
        var status = await _coordinator.GetStatusAsync(executionId, _userId, cancellationToken).ConfigureAwait(false);
        return status.ConversationId == expectedConversationId
            ? status
            : throw new AgwException(ErrorCodes.DurableExecutionNotFound);
    }

    private void SetActiveExecution(Guid? executionId)
    {
        lock (_stateLock)
        {
            _activeExecutionId = executionId;
        }
    }

    /// <summary>
    /// 游标是已经收到的 turnSequence；为空时从头读取。
    /// The cursor is the turnSequence already received; an empty cursor reads from the start.
    /// </summary>
    internal static long ParseCursor(string? cursor) =>
        string.IsNullOrWhiteSpace(cursor) ? 0
        : long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ? sequence
        : throw new AgwException(ErrorCodes.InvalidParam, $"Execution stream cursor '{cursor}' is invalid.");

    private Task SendErrorAsync(string message) =>
        _messageSink
            .WriteAsync(CreateMessage(new AgwErrorContent { Content = message }), CancellationToken.None)
            .AsTask();

    private Task SendSystemMessageAsync(string message) =>
        _messageSink
            .WriteAsync(CreateMessage(new AgwTextContent { Content = message }), CancellationToken.None)
            .AsTask();

    private static AgwMessage CreateMessage(AgwContent content) =>
        new(Guid.CreateVersion7().ToString("D"), Constants.DefaultAgentAuthor, AiRole.System, [content]);
}
