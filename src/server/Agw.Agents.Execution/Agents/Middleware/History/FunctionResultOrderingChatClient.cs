using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.History;

/// <summary>
/// <para>在历史加载前后整理工具调用与结果的位置，保持模型协议要求的相邻关系。</para>
/// <para>Orders function calls and results around history loading to preserve protocol-required adjacency.</para>
/// </summary>
internal sealed class FunctionResultOrderingChatClient : DelegatingChatClient
{
    /// <summary>
    /// <para>创建 FunctionResultOrderingChatClient 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes FunctionResultOrderingChatClient with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerClient">
    /// <para>接收处理后请求的内层聊天客户端。</para>
    /// <para>Inner chat client receiving the processed request.</para>
    /// </param>
    public FunctionResultOrderingChatClient(IChatClient innerClient)
        : base(innerClient) { }

    /// <summary>
    /// <para>整理调用和结果的相邻位置后调用内层模型，保留其响应内容。</para>
    /// <para>Calls the inner model after restoring call/result adjacency, preserving its response content.</para>
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
    /// <para>完成当前包装层处理后的完整响应。</para>
    /// <para>Complete response after processing by this wrapper.</para>
    /// </returns>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetResponseAsync(OrderMessages(messages), options, cancellationToken);

    /// <summary>
    /// <para>整理调用和结果的相邻位置后调用内层模型，保留其响应内容。</para>
    /// <para>Calls the inner model after restoring call/result adjacency, preserving its response content.</para>
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
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetStreamingResponseAsync(OrderMessages(messages), options, cancellationToken);

    /// <summary>
    /// <para>先将进行中的工具结果移到注入上下文之前，再把匹配结果移到调用之后，遇下一条普通 Assistant 消息即停止跨越。</para>
    /// <para>Moves in-flight results before injected context, then places matching results after their calls without crossing the next ordinary assistant message.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <returns>
    /// <para>按调用与结果关系重排后的消息序列。</para>
    /// <para>Message sequence reordered by call/result relationships.</para>
    /// </returns>
    private static IEnumerable<ChatMessage> OrderMessages(IEnumerable<ChatMessage> messages)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        // 区分注入上下文前缀与进行中的工具结果，兼容历史加载前后的两次调用。
        // Separate injected-context prefixes from in-flight tool results for both sides of history loading.
        var contextCount = messageList
            .TakeWhile(message =>
                message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider
            )
            .Count();
        var resultCount = messageList
            .Skip(contextCount)
            .TakeWhile(message =>
                message.Role == ChatRole.Tool && message.Contents.OfType<FunctionResultContent>().Any()
            )
            .Count();
        if (contextCount > 0 && resultCount > 0)
        {
            // 历史加载前，将进行中的工具结果放到注入上下文之前。
            // Before history is loaded, only injected context may precede the in-flight results.
            messageList = messageList
                .Skip(contextCount)
                .Take(resultCount)
                .Concat(messageList.Take(contextCount))
                .Concat(messageList.Skip(contextCount + resultCount))
                .ToList();
        }
        var ordered = new List<ChatMessage>(messageList.Count);
        for (var index = 0; index < messageList.Count; index++)
        {
            var message = messageList[index];
            ordered.Add(message);
            if (message.Role != ChatRole.Assistant)
                continue;
            var callIds = message
                .Contents.OfType<FunctionCallContent>()
                .Select(call => call.CallId)
                .ToHashSet(StringComparer.Ordinal);
            if (callIds.Count == 0)
                continue;

            var deferred = new List<ChatMessage>();
            // 在合并后的历史与请求中匹配结果，只跨越已知调用的间隔，并在下一条普通 Assistant 响应处停止。
            // Work on the merged history/request, stopping at the next assistant response.
            // A result may cross intervening user/context messages only when its call is known.
            while (
                index + 1 < messageList.Count
                && (
                    messageList[index + 1].Role != ChatRole.Assistant
                    || messageList[index + 1].GetAgentRequestMessageSourceType()
                        == AgentRequestMessageSourceType.AIContextProvider
                )
            )
            {
                var next = messageList[++index];
                var results = next.Contents.OfType<FunctionResultContent>().ToList();
                if (
                    next.Role == ChatRole.Tool
                    && results.Count > 0
                    && results.All(result => callIds.Contains(result.CallId))
                )
                    ordered.Add(next);
                else
                    deferred.Add(next);
            }
            ordered.AddRange(deferred);
        }
        return ordered;
    }
}
