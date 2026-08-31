using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

/// <summary>
/// Persists External Agent requests immediately and response updates in bounded streaming batches.
/// </summary>
internal sealed class ExternalAgentChatHistoryAgent : DelegatingAIAgent
{
    internal const int ResponseBatchSize = 20;
    internal static readonly TimeSpan ResponseFlushInterval = TimeSpan.FromSeconds(1);

    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// 创建负责持久化 External Agent 聊天历史的包装器。
    /// </summary>
    /// <param name="innerAgent">被包装的 External Agent。</param>
    /// <param name="chatHistoryProvider">用于读写聊天历史的 Provider。</param>
    /// <param name="timeProvider">用于控制定时刷新间隔的时间 Provider。</param>
    /// <param name="logger">用于记录持久化或释放异常的日志记录器。</param>
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
    /// 执行非流式调用，并分别持久化请求消息和最终响应消息。
    /// </summary>
    /// <param name="messages">本轮请求消息。</param>
    /// <param name="session">本轮使用的 Agent 会话；为空时创建新会话。</param>
    /// <param name="options">Agent 运行选项。</param>
    /// <param name="cancellationToken">用于取消 Agent 执行的令牌。</param>
    /// <returns>External Agent 返回的完整响应。</returns>
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var requestMessages = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        await PersistAsync(safeSession, CreatePersistableRequestMessages(requestMessages), [], CancellationToken.None)
            .ConfigureAwait(false);

        var response = await InnerAgent
            .RunAsync(requestMessages, safeSession, options, cancellationToken)
            .ConfigureAwait(false);

        var responseMessages = response.Messages.Select(CreatePersistableMessage).OfType<ChatMessage>().ToList();
        await PersistAsync(safeSession, [], responseMessages, CancellationToken.None).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// 执行流式调用，将请求立即持久化，并按数量或时间间隔批量持久化响应更新。
    /// </summary>
    /// <param name="messages">本轮请求消息。</param>
    /// <param name="session">本轮使用的 Agent 会话；为空时创建新会话。</param>
    /// <param name="options">Agent 运行选项。</param>
    /// <param name="cancellationToken">用于取消 Agent 执行的令牌。</param>
    /// <returns>按原始顺序返回的 External Agent 响应更新流。</returns>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var requestMessages = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        // 请求消息必须先于任何响应事件写入，确保长时间运行或中途断开的 Turn 仍可被追溯。
        await PersistAsync(safeSession, CreatePersistableRequestMessages(requestMessages), [], CancellationToken.None)
            .ConfigureAwait(false);

        var responseBuffer = new List<ChatMessage>(ResponseBatchSize);

        // 使用独立的关联令牌控制底层流，以便消费方提前释放时主动终止 External Agent。
        using var innerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
        Task<bool>? moveNextTask = null;
        CancellationTokenSource? flushDelayCancellation = null;
        Task? flushDelayTask = null;
        Exception? executionFailure = null;

        try
        {
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
                            // 数量达到上限时优先刷新，并终止当前批次尚未完成的计时器。
                            StopFlushDelay(ref flushDelayCancellation, ref flushDelayTask);
                            await FlushAsync(safeSession, responseBuffer).ConfigureAwait(false);
                        }
                        else if (flushDelayTask == null)
                        {
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
    /// 将响应缓冲区中的消息持久化，并在成功后清空缓冲区。
    /// </summary>
    /// <param name="session">当前 Agent 会话。</param>
    /// <param name="responseBuffer">待持久化的响应消息缓冲区。</param>
    private async Task FlushAsync(AgentSession session, List<ChatMessage> responseBuffer)
    {
        if (responseBuffer.Count == 0)
        {
            return;
        }

        await PersistAsync(session, [], responseBuffer, CancellationToken.None).ConfigureAwait(false);
        responseBuffer.Clear();
    }

    /// <summary>
    /// 通过统一的聊天历史 Provider 持久化一次请求或响应消息批次。
    /// </summary>
    /// <param name="session">当前 Agent 会话。</param>
    /// <param name="requestMessages">本批次包含的请求消息。</param>
    /// <param name="responseMessages">本批次包含的响应消息。</param>
    /// <param name="cancellationToken">用于取消持久化操作的令牌。</param>
    /// <returns>表示持久化操作的异步结果。</returns>
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
    /// 将非空且可展示的流式响应更新转换为可持久化的聊天消息。
    /// </summary>
    /// <param name="update">External Agent 返回的响应更新。</param>
    /// <returns>可持久化的聊天消息；更新不包含有效内容时返回 <see langword="null" />。</returns>
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
    /// 复制非空且可展示的完整响应消息，并移除不应写入历史的原始传输对象。
    /// </summary>
    /// <param name="message">External Agent 返回的完整响应消息。</param>
    /// <returns>可持久化的聊天消息；消息不包含有效内容时返回 <see langword="null" />。</returns>
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
    /// Removes invocation-only context messages before External Agent requests are persisted.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> CreatePersistableRequestMessages(IEnumerable<ChatMessage> messages) =>
        messages
            .Where(message =>
                message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider
            )
            .ToList();

    /// <summary>
    /// 将仅用于界面展示的 System、User 或 Tool 响应标记为不参与模型历史和跨 Agent 交接。
    /// </summary>
    /// <param name="message">要检查并按需标记的响应消息。</param>
    private static void MarkDisplayOnlyMessage(ChatMessage message)
    {
        if (message.Role == ChatRole.System || message.Role == ChatRole.User || message.Role == ChatRole.Tool)
        {
            ConversationHistoryMetadata.ExcludeFromModelHistory(message);
        }
    }

    /// <summary>
    /// 取消并释放当前响应批次的定时刷新任务。
    /// </summary>
    /// <param name="flushDelayCancellation">定时刷新任务使用的取消源。</param>
    /// <param name="flushDelayTask">当前定时刷新任务。</param>
    private static void StopFlushDelay(ref CancellationTokenSource? flushDelayCancellation, ref Task? flushDelayTask)
    {
        flushDelayCancellation?.Cancel();
        flushDelayCancellation?.Dispose();
        flushDelayCancellation = null;
        flushDelayTask = null;
    }
}
