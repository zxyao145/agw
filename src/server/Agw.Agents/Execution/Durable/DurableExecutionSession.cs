using Agw.Agents.Execution.Commands.Exec;
using Agw.Agents.Execution.Commands.Hitl;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 一条 SignalR connection 对 durable execution 的临时 attachment。
/// 断开连接只停止订阅，不拥有也不终止 PostgreSQL 中的 execution。
/// </summary>
internal sealed class DurableExecutionSession : IAsyncDisposable
{
    private readonly string _userName;
    private readonly IExecutionMessageSink _messageSink;
    private readonly CancellationToken _hostToken;
    private readonly DurableExecutionCoordinator _coordinator;
    private readonly object _stateLock = new();
    private Guid? _activeExecutionId;
    private CancellationTokenSource? _subscriptionCts;
    private Task _subscriptionTask = Task.CompletedTask;

    /// <summary>
    /// 创建当前用户和 SignalR connection 对应的 durable attachment。
    /// </summary>
    public DurableExecutionSession(
        string userName,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken,
        DurableExecutionCoordinator coordinator
    )
    {
        _userName = userName;
        _messageSink = messageSink;
        _hostToken = hostToken;
        _coordinator = coordinator;
    }

    /// <summary>
    /// 指示当前 connection 是否附着到未结束的 durable execution。
    /// </summary>
    public bool HasActiveExecution => ActiveExecutionId.HasValue;

    /// <summary>
    /// 获取当前附着的 executionId。
    /// </summary>
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

    /// <summary>
    /// 使用客户端提供或服务端生成的稳定 executionId 启动并立即附着执行。
    /// </summary>
    public async Task StartAsync(
        ExecCommand command,
        TaskProjection task,
        ExecutionSettings settings,
        CancellationToken cancellationToken
    )
    {
        var executionId =
            command.ExecutionId is { } requestedExecutionId && requestedExecutionId != Guid.Empty
                ? requestedExecutionId
                : Guid.CreateVersion7();
        command.ExecutionId = executionId;
        await _coordinator
            .StartAsync(executionId, _userName, command, task, settings, cancellationToken)
            .ConfigureAwait(false);
        await AttachAsync(executionId, cursor: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 鉴权并重新附着已有 execution，从指定 event stream cursor 继续订阅。
    /// </summary>
    public async Task AttachAsync(Guid executionId, string? cursor, CancellationToken cancellationToken)
    {
        if (executionId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "executionId is required.");
        }

        // 先完成 PostgreSQL 鉴权，再启动由协调器自行创建 DbContext scope 的后台 pump。
        var status = await _coordinator.GetStatusAsync(executionId, _userName, cancellationToken).ConfigureAwait(false);
        await StopSubscriptionAsync().ConfigureAwait(false);
        await SendTurnStateAsync(status, cancellationToken).ConfigureAwait(false);
        SetActiveExecution(IsTerminal(status.Status) ? null : executionId);
        if (IsTerminal(status.Status))
        {
            return;
        }

        var subscriptionCts = CancellationTokenSource.CreateLinkedTokenSource(_hostToken);
        _subscriptionCts = subscriptionCts;
        _subscriptionTask = PumpAsync(executionId, cursor, subscriptionCts.Token);
    }

    /// <summary>
    /// 终止显式指定或当前附着的 execution，并向当前 connection 发布终态。
    /// </summary>
    public async Task InterruptAsync(Guid? executionId, string? reason, CancellationToken cancellationToken)
    {
        var targetExecutionId = executionId ?? ActiveExecutionId;
        if (!targetExecutionId.HasValue)
        {
            await SendSystemMessageAsync(reason ?? "No active request is currently running.").ConfigureAwait(false);
            await _messageSink
                .WriteAsync(TurnMessageFactory.CreateFinished("interrupted"), CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        var interrupted = await _coordinator
            .InterruptAsync(targetExecutionId.Value, _userName, reason, cancellationToken)
            .ConfigureAwait(false);
        if (interrupted)
        {
            await StopSubscriptionAsync().ConfigureAwait(false);
            await _messageSink
                .WriteAsync(
                    TurnMessageFactory.CreateFinished("interrupted", targetExecutionId.Value),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        else
        {
            var status = await _coordinator
                .GetStatusAsync(targetExecutionId.Value, _userName, cancellationToken)
                .ConfigureAwait(false);
            await SendTurnStateAsync(status, cancellationToken).ConfigureAwait(false);
        }

        SetActiveExecution(null);
    }

    /// <summary>
    /// 将 HumanResponseCommand 持久化到 PostgreSQL，并重新展示同批次中尚未回答的请求。
    /// </summary>
    public async Task RespondAsync(HumanResponseCommand command, CancellationToken cancellationToken)
    {
        var executionId = command.ExecutionId ?? ActiveExecutionId;
        if (!executionId.HasValue)
        {
            await SendSystemMessageAsync("No matching durable human interaction is waiting for this response.")
                .ConfigureAwait(false);
            return;
        }

        await _coordinator
            .SubmitHumanResponseAsync(
                new SubmitDurableHumanResponseRequest(
                    executionId.Value,
                    command.RequestId,
                    command.Approved,
                    command.ResponseText,
                    command.ApprovalScope,
                    command.ResponseData
                ),
                _userName,
                cancellationToken
            )
            .ConfigureAwait(false);
        var remaining = await _coordinator
            .GetPendingAsync(executionId.Value, _userName, cancellationToken)
            .ConfigureAwait(false);
        foreach (var interaction in remaining)
        {
            if (string.Equals(interaction.RequestId, command.RequestId, StringComparison.Ordinal))
            {
                continue;
            }

            await _messageSink
                .WriteAsync(DurableHumanInteractionMapper.ToMessage(interaction, executionId.Value), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 原子创建 checkpoint 恢复分支，并将当前 connection 附着到新 execution。
    /// </summary>
    public async Task ResumeCheckpointAsync(
        Guid occurrenceId,
        Guid resumeExecutionId,
        Guid projectId,
        string contextId,
        Guid agentflowId,
        CancellationToken cancellationToken
    )
    {
        await _coordinator
            .ResumeCheckpointAsync(
                occurrenceId,
                resumeExecutionId,
                projectId,
                contextId,
                agentflowId,
                _userName,
                cancellationToken
            )
            .ConfigureAwait(false);
        await AttachAsync(resumeExecutionId, cursor: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 连接断开时只取消消息订阅；执行仍由 distributed worker 持续托管。
    /// </summary>
    public void PrepareForDetach() => _subscriptionCts?.Cancel();

    /// <summary>
    /// 停止当前订阅并释放 connection 本地 attachment 状态。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopSubscriptionAsync().ConfigureAwait(false);
        SetActiveExecution(null);
    }

    /// <summary>
    /// 把协调器产生的回放和降级消息持续转发到当前 SignalR sink。
    /// </summary>
    private async Task PumpAsync(Guid executionId, string? cursor, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (
                var entry in _coordinator.ReadAsync(executionId, cursor, cancellationToken).ConfigureAwait(false)
            )
            {
                await _messageSink.WriteAsync(entry.Message, cancellationToken).ConfigureAwait(false);
                if (IsTurnFinished(entry.Message))
                {
                    if (ActiveExecutionId == executionId)
                    {
                        SetActiveExecution(null);
                    }
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
    /// 在线程安全边界内更新当前 execution attachment。
    /// </summary>
    private void SetActiveExecution(Guid? executionId)
    {
        lock (_stateLock)
        {
            _activeExecutionId = executionId;
        }
    }

    /// <summary>
    /// 把 durable 状态转换为现有 turn-start/turn-finished 控制协议。
    /// </summary>
    private Task SendTurnStateAsync(DurableExecutionStatusResponse status, CancellationToken cancellationToken)
    {
        var message = status.Status switch
        {
            DurableExecutionStatus.Completed => TurnMessageFactory.CreateFinished("completed", status.ExecutionId),
            DurableExecutionStatus.Failed => TurnMessageFactory.CreateFinished("failed", status.ExecutionId),
            DurableExecutionStatus.Interrupted => TurnMessageFactory.CreateFinished("interrupted", status.ExecutionId),
            _ => TurnMessageFactory.CreateStarted(status.ExecutionId, status.StreamingScopeId),
        };
        return _messageSink.WriteAsync(message, cancellationToken).AsTask();
    }

    /// <summary>
    /// 向当前 connection 发送错误内容。
    /// </summary>
    private Task SendErrorAsync(string message) =>
        _messageSink
            .WriteAsync(CreateMessage(new AgwErrorContent { Content = message }), CancellationToken.None)
            .AsTask();

    /// <summary>
    /// 向当前 connection 发送普通系统提示。
    /// </summary>
    private Task SendSystemMessageAsync(string message) =>
        _messageSink
            .WriteAsync(CreateMessage(new AgwTextContent { Content = message }), CancellationToken.None)
            .AsTask();

    /// <summary>
    /// 创建不参与 durable 状态判定的系统消息。
    /// </summary>
    private static AgwMessage CreateMessage(AgwContent content) =>
        new(Guid.CreateVersion7().ToString("D"), Constants.DefaultAgentAuthor, AiRole.System, [content]);

    /// <summary>
    /// 判断状态是否已结束，不应再启动消息订阅。
    /// </summary>
    private static bool IsTerminal(DurableExecutionStatus status) =>
        status
            is DurableExecutionStatus.Completed
                or DurableExecutionStatus.Failed
                or DurableExecutionStatus.Interrupted;

    /// <summary>
    /// 判断消息是否为 turn-finished 控制消息。
    /// </summary>
    private static bool IsTurnFinished(AgwMessage message) =>
        message.AdditionalProperties != null
        && message.AdditionalProperties.TryGetValue("type", out var type)
        && string.Equals(type as string, "turn-finished", StringComparison.Ordinal);
}
