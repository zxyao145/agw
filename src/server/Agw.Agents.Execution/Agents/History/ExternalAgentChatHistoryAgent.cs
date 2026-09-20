using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// <para>将外部 Agent 的响应保存为可展示的历史消息，并对流式响应进行有界批量写入。</para>
/// <para>Persists external-agent responses as displayable history and writes streaming responses in bounded batches.</para>
/// </summary>
/// <remarks>
/// <para>流式批次达到 20 条或一秒窗口即刷新。退出时尝试刷新尾部并终止内层枚举；保留执行异常优先级，其次为持久化异常和释放异常。</para>
/// <para>Flushes streaming batches at 20 messages or a one-second window. On exit, attempts a final flush and stops inner enumeration, prioritizing execution failures over persistence and disposal failures.</para>
/// </remarks>
internal sealed class ExternalAgentChatHistoryAgent : DelegatingAIAgent
{
    internal const int ResponseBatchSize = 20;
    internal static readonly TimeSpan ResponseFlushInterval = TimeSpan.FromSeconds(1);

    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// <para>创建 ExternalAgentChatHistoryAgent 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes ExternalAgentChatHistoryAgent with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="chatHistoryProvider">
    /// <para>负责写入外部 Agent 历史的 Provider。</para>
    /// <para>Provider responsible for persisting external-agent history.</para>
    /// </param>
    /// <param name="timeProvider">
    /// <para>提供批量刷新计时的时钟。</para>
    /// <para>Clock used for batch-flush timing.</para>
    /// </param>
    /// <param name="logger">
    /// <para>记录执行、兼容处理或清理错误的日志器。</para>
    /// <para>Logger for execution, compatibility handling, or cleanup errors.</para>
    /// </param>
    internal ExternalAgentChatHistoryAgent(
        AIAgent innerAgent,
        ChatHistoryProvider chatHistoryProvider,
        TimeProvider timeProvider,
        ILogger logger
    )
        : base(innerAgent)
    {
        _chatHistoryProvider = chatHistoryProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// <para>执行外部 Agent，并将可展示的响应复制到历史存储。</para>
    /// <para>Executes the external agent and copies displayable responses to history storage.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；为空时向内层 Agent 创建新会话。</para>
    /// <para>Current SDK session; null creates a new session through the inner agent.</para>
    /// </param>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>完成当前包装层处理后的完整响应。</para>
    /// <para>Complete response after processing by this wrapper.</para>
    /// </returns>
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var requestMessages = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        var response = await InnerAgent
            .RunAsync(requestMessages, safeSession, options, cancellationToken)
            .ConfigureAwait(false);

        var responseMessages = response.Messages.Select(CreatePersistableMessage).OfType<ChatMessage>().ToList();
        await PersistAsync(safeSession, [], responseMessages, CancellationToken.None).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// <para>转发流式响应，并按条数或计时窗口刷新历史；退出时刷新尾部、取消读取并释放枚举器。</para>
    /// <para>Forwards streaming responses and flushes history by batch size or timer, then flushes the tail, cancels reads, and disposes the enumerator on exit.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；为空时向内层 Agent 创建新会话。</para>
    /// <para>Current SDK session; null creates a new session through the inner agent.</para>
    /// </param>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>经当前包装层处理后按顺序产生的响应更新流。</para>
    /// <para>Ordered response-update stream after processing by this wrapper.</para>
    /// </returns>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var requestMessages = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        var responseBuffer = new List<ChatMessage>(ResponseBatchSize);

        // Use a linked token to stop the external agent when the consumer disposes early.
        // 使用独立的关联令牌控制底层流，以便消费方提前释放时主动终止 External Agent。
        using var innerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
        Task<bool>? moveNextTask = null;
        CancellationTokenSource? flushDelayCancellation = null;
        Task? flushDelayTask = null;
        Exception? executionFailure = null;

        try
        {
            // Drive enumeration manually so the one-second flush timer can run while awaiting an event.
            // 手动驱动枚举器，以便在等待下一条事件时同时响应一秒刷新计时器。
            enumerator = InnerAgent
                .RunStreamingAsync(requestMessages, safeSession, options, innerCancellation.Token)
                .GetAsyncEnumerator(innerCancellation.Token);

            while (true)
            {
                AgentResponseUpdate update;
                try
                {
                    moveNextTask ??= enumerator.MoveNextAsync().AsTask();
                    if (flushDelayTask != null)
                    {
                        // Race the next event against the flush timer; flush the current batch when the timer wins.
                        // 下一条事件与刷新计时器竞速；计时器先完成时立即写入当前微批。
                        var completedTask = await Task.WhenAny(moveNextTask, flushDelayTask).ConfigureAwait(false);
                        if (completedTask == flushDelayTask)
                        {
                            await flushDelayTask.ConfigureAwait(false);
                            StopFlushDelay(ref flushDelayCancellation, ref flushDelayTask);
                            await FlushAsync(safeSession, responseBuffer).ConfigureAwait(false);
                            continue;
                        }
                    }

                    if (!await moveNextTask.ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                    moveNextTask = null;
                    var responseMessage = CreatePersistableMessage(update);
                    if (responseMessage != null)
                    {
                        responseBuffer.Add(responseMessage);
                        if (responseBuffer.Count >= ResponseBatchSize)
                        {
                            // Flush at the batch limit and stop the outstanding timer for that batch.
                            // 数量达到上限时优先刷新，并终止当前批次尚未完成的计时器。
                            StopFlushDelay(ref flushDelayCancellation, ref flushDelayTask);
                            await FlushAsync(safeSession, responseBuffer).ConfigureAwait(false);
                        }
                        else if (flushDelayTask == null)
                        {
                            // Start the one-second window when the first persistable event enters an empty batch.
                            // 第一条可持久化事件进入空缓冲区时，启动该批次的一秒刷新窗口。
                            flushDelayCancellation = new CancellationTokenSource();
                            flushDelayTask = Task.Delay(
                                ResponseFlushInterval,
                                _timeProvider,
                                flushDelayCancellation.Token
                            );
                        }
                    }
                }
                catch (Exception exception)
                {
                    executionFailure = exception;
                    throw;
                }

                yield return update;
            }
        }
        finally
        {
            // Flush remaining events on completion, cancellation, failure, or early consumer disposal.
            // 无论正常完成、取消、异常还是消费方提前释放，都先写入尚未达到阈值的剩余事件。
            StopFlushDelay(ref flushDelayCancellation, ref flushDelayTask);
            Exception? persistenceFailure = null;
            try
            {
                await FlushAsync(safeSession, responseBuffer).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                persistenceFailure = exception;
            }

            Exception? disposeFailure = null;
            try
            {
                // Cancel and await an in-flight MoveNext before disposing to avoid disposal concurrent with a read.
                // 先取消并等待进行中的 MoveNext，再释放枚举器，避免底层流仍在读取时并发释放。
                innerCancellation.Cancel();
                if (moveNextTask is { IsCompleted: false })
                {
                    try
                    {
                        await moveNextTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (innerCancellation.IsCancellationRequested) { }
                }
            }
            catch (Exception exception)
            {
                disposeFailure = exception;
            }

            if (enumerator != null)
            {
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    disposeFailure =
                        disposeFailure == null ? exception : new AggregateException(disposeFailure, exception);
                }
            }

            // Preserve an existing execution failure; otherwise propagate persistence or disposal failures.
            // 已有执行异常时保留原始异常；否则让持久化或释放异常使本轮执行失败。
            if (executionFailure != null)
            {
                if (persistenceFailure != null)
                {
                    _logger.LogError(
                        persistenceFailure,
                        "External Agent history persistence failed while preserving an execution failure."
                    );
                }
                if (disposeFailure != null)
                {
                    _logger.LogError(
                        disposeFailure,
                        "External Agent stream disposal failed while preserving an execution failure."
                    );
                }
            }
            else if (persistenceFailure != null)
            {
                if (disposeFailure != null)
                {
                    _logger.LogError(
                        disposeFailure,
                        "External Agent stream disposal failed while preserving a history persistence failure."
                    );
                }

                ExceptionDispatchInfo.Capture(persistenceFailure).Throw();
            }
            else if (disposeFailure != null)
            {
                ExceptionDispatchInfo.Capture(disposeFailure).Throw();
            }
        }
    }

    /// <summary>
    /// <para>持久化当前响应批次，只有成功后才清空缓冲区。</para>
    /// <para>Persists the current response batch and clears the buffer only after success.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话及其状态。</para>
    /// <para>Current SDK session and its state.</para>
    /// </param>
    /// <param name="responseBuffer">
    /// <para>待提交的响应缓冲区，成功写入后原地清空。</para>
    /// <para>Pending response buffer, cleared in place after successful persistence.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private async Task FlushAsync(AgentSession session, List<ChatMessage> responseBuffer)
    {
        if (responseBuffer.Count == 0)
        {
            return;
        }

        await PersistAsync(session, [], responseBuffer, CancellationToken.None).ConfigureAwait(false);
        // 仅在写入成功后清空；失败时保留缓冲，供上层按原异常策略处理。
        // Clear only after a successful write; failures retain the buffer for the established error-handling path.
        responseBuffer.Clear();
    }

    /// <summary>
    /// <para>通过历史 Provider 的执行完成回调提交请求和响应消息批次。</para>
    /// <para>Submits request and response batches through the history provider's invocation-completion callback.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话及其状态。</para>
    /// <para>Current SDK session and its state.</para>
    /// </param>
    /// <param name="requestMessages">
    /// <para>当前操作使用的请求消息集合。</para>
    /// <para>Request messages used by the current operation.</para>
    /// </param>
    /// <param name="responseMessages">
    /// <para>本批次需要保存的响应消息。</para>
    /// <para>Response messages to persist in this batch.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private ValueTask PersistAsync(
        AgentSession session,
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage> responseMessages,
        CancellationToken cancellationToken
    ) =>
        _chatHistoryProvider.InvokedAsync(
            new ChatHistoryProvider.InvokedContext(this, session, requestMessages, responseMessages),
            cancellationToken
        );

    /// <summary>
    /// <para>移除空展示内容，复制必要的响应元数据，并将展示专用消息排除出模型历史。</para>
    /// <para>Removes blank display content, copies required response metadata, and excludes display-only messages from model history.</para>
    /// </summary>
    /// <param name="update">
    /// <para>当前执行产生的响应更新。</para>
    /// <para>Response update produced by the current execution.</para>
    /// </param>
    /// <returns>
    /// <para>可持久化的消息副本；内容被全部过滤时为空。</para>
    /// <para>Persistable message copy, or null when all content is filtered out.</para>
    /// </returns>
    private static ChatMessage? CreatePersistableMessage(AgentResponseUpdate update)
    {
        var contents = update.Contents.WithoutBlankTextualContent(update.AdditionalProperties);
        if (contents.Count == 0)
        {
            return null;
        }

        var role = update.Role ?? ChatRole.System;
        var message = new ChatMessage(role, contents)
        {
            AuthorName = update.AuthorName,
            CreatedAt = update.CreatedAt,
            MessageId = update.MessageId,
            AdditionalProperties =
                update.AdditionalProperties == null
                    ? null
                    : new AdditionalPropertiesDictionary(update.AdditionalProperties),
        };
        MarkDisplayOnlyMessage(message);
        return message;
    }

    /// <summary>
    /// <para>移除空展示内容，复制必要的响应元数据，并将展示专用消息排除出模型历史。</para>
    /// <para>Removes blank display content, copies required response metadata, and excludes display-only messages from model history.</para>
    /// </summary>
    /// <param name="message">
    /// <para>待检查、复制或补充元数据的消息。</para>
    /// <para>Message to inspect, copy, or annotate.</para>
    /// </param>
    /// <returns>
    /// <para>可持久化的消息副本；内容被全部过滤时为空。</para>
    /// <para>Persistable message copy, or null when all content is filtered out.</para>
    /// </returns>
    internal static ChatMessage? CreatePersistableMessage(ChatMessage message)
    {
        var contents = message.Contents.WithoutBlankTextualContent(message.AdditionalProperties);
        if (contents.Count == 0)
        {
            return null;
        }

        var persistedMessage = message.Clone();
        persistedMessage.Contents = contents;
        persistedMessage.RawRepresentation = null;
        if (message.AdditionalProperties != null)
        {
            persistedMessage.AdditionalProperties = new AdditionalPropertiesDictionary(message.AdditionalProperties);
        }

        MarkDisplayOnlyMessage(persistedMessage);
        return persistedMessage;
    }

    /// <summary>
    /// <para>将 System、User 和 Tool 响应标记为不参与模型历史及跨 Agent 交接。</para>
    /// <para>Marks system, user, and tool responses as excluded from model history and cross-agent handoff.</para>
    /// </summary>
    /// <param name="message">
    /// <para>待检查、复制或补充元数据的消息。</para>
    /// <para>Message to inspect, copy, or annotate.</para>
    /// </param>
    private static void MarkDisplayOnlyMessage(ChatMessage message)
    {
        if (message.Role == ChatRole.System || message.Role == ChatRole.User || message.Role == ChatRole.Tool)
        {
            ConversationHistoryMetadata.ExcludeFromModelHistory(message);
        }
    }

    /// <summary>
    /// <para>取消并释放当前批次计时器，同时清空调用方持有的计时器引用。</para>
    /// <para>Cancels and disposes the current batch timer and clears the caller's timer references.</para>
    /// </summary>
    /// <param name="flushDelayCancellation">
    /// <para>当前刷新计时器的取消源，方法结束时清空引用。</para>
    /// <para>Cancellation source for the current flush timer, cleared on completion.</para>
    /// </param>
    /// <param name="flushDelayTask">
    /// <para>当前批次的刷新等待任务，方法结束时清空引用。</para>
    /// <para>Current batch's flush-delay task, cleared on completion.</para>
    /// </param>
    private static void StopFlushDelay(ref CancellationTokenSource? flushDelayCancellation, ref Task? flushDelayTask)
    {
        flushDelayCancellation?.Cancel();
        flushDelayCancellation?.Dispose();
        flushDelayCancellation = null;
        flushDelayTask = null;
    }
}
