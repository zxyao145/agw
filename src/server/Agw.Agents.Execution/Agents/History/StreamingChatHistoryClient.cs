using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// <para>在 SDK 每次模型调用的历史加载之后，保存输入并捕获流式响应增量。</para>
/// <para>Captures inputs and streaming response deltas after the SDK's per-call history loading stage.</para>
/// </summary>
/// <remarks>
/// <para>必须位于每次模型调用的历史加载之后。每段响应先提交给历史收集器再转发；缺少运行会话时直接转发。</para>
/// <para>Must run after per-model-call history loading. Each delta is appended to the history collector before forwarding; without a run session, updates pass through.</para>
/// </remarks>
internal sealed class StreamingChatHistoryClient : DelegatingChatClient
{
    private readonly IStreamingConversationHistoryProvider _history;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// <para>创建 StreamingChatHistoryClient 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes StreamingChatHistoryClient with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerClient">
    /// <para>接收处理后请求的内层聊天客户端。</para>
    /// <para>Inner chat client receiving the processed request.</para>
    /// </param>
    /// <param name="history">
    /// <para>为模型调用建立流式响应记录的历史服务。</para>
    /// <para>History service creating streaming response records for model calls.</para>
    /// </param>
    /// <param name="timeProvider">Clock used when a provider omits message timestamps.</param>
    public StreamingChatHistoryClient(
        IChatClient innerClient,
        IStreamingConversationHistoryProvider history,
        TimeProvider? timeProvider = null
    )
        : base(innerClient)
    {
        _history = history;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var timestamp = response.CreatedAt ?? _timeProvider.GetUtcNow();
        foreach (var message in response.Messages)
            MessageTimestampMetadata.EnsureCreatedAt(message, timestamp);
        return response;
    }

    /// <summary>
    /// <para>开始响应历史记录，逐段保存模型更新后按原顺序转发。</para>
    /// <para>Starts response-history capture and appends each model update before forwarding it in order.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
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
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var context = AIAgent.CurrentRunContext;
        var input = messages.ToList();
        var timestamps = new ResponseMessageTimestamps(_timeProvider);
        // 此时历史已由 SDK 加载；用当前会话建立本次模型响应的增量记录。
        // History is already loaded by the SDK; use the current session to start this model response's delta record.
        var history =
            context?.Session == null
                ? null
                : await _history
                    .BeginStreamingResponseAsync(context.Agent, context.Session, input, cancellationToken)
                    .ConfigureAwait(false);
        await foreach (
            var update in base.GetStreamingResponseAsync(input, options, cancellationToken).ConfigureAwait(false)
        )
        {
            // 先捕获不可变历史增量再转发，避免消费者推进或修改更新后再读取。
            // Capture history deltas before forwarding, rather than reading updates after consumer advancement or mutation.
            timestamps.Stamp(update);
            if (history is NormalizedChatHistoryProvider.Capture capture)
            {
                var handled = await capture.AppendUpdateAsync(update, cancellationToken).ConfigureAwait(false);
                foreach (var normalized in capture.Drain())
                    yield return new ChatResponseUpdate(normalized.Role, normalized.Contents)
                    {
                        MessageId = normalized.MessageId,
                        AuthorName = normalized.AuthorName,
                        CreatedAt = normalized.CreatedAt,
                        AdditionalProperties = normalized.AdditionalProperties,
                        FinishReason = update.FinishReason,
                        ModelId = update.ModelId,
                        ResponseId = update.ResponseId,
                    };
                // 未被任何操作认领的更新原样下发，避免归并链路吞掉内容。
                // An update no operation claimed still has to reach the consumer untouched.
                if (!handled)
                    yield return update;
                else if (update.Contents.OfType<UsageContent>().Any())
                    yield return new ChatResponseUpdate
                    {
                        Role = update.Role,
                        Contents = update.Contents.OfType<UsageContent>().Cast<AIContent>().ToList(),
                        FinishReason = update.FinishReason,
                        ModelId = update.ModelId,
                        ResponseId = update.ResponseId,
                    };
            }
            else
            {
                if (history != null)
                    await history.AppendAsync(update, cancellationToken).ConfigureAwait(false);
                yield return update;
            }
        }
    }
}
