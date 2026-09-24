using Agw.Agents.Execution.Context;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.ModelInput;

/// <summary>
/// <para>过滤模型无法接收的临时内容与 Agw 执行数据，同时保留仍需发送给 Provider 的 MCP 审批响应。</para>
/// <para>Filters transient content and Agw execution data that cannot be sent to the model while retaining provider-facing MCP approval responses.</para>
/// </summary>
internal sealed class ModelInputFilteringChatClient : DelegatingChatClient
{
    /// <summary>
    /// <para>创建 ModelInputFilteringChatClient 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes ModelInputFilteringChatClient with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerClient">
    /// <para>接收处理后请求的内层聊天客户端。</para>
    /// <para>Inner chat client receiving the processed request.</para>
    /// </param>
    public ModelInputFilteringChatClient(IChatClient innerClient)
        : base(innerClient) { }

    /// <summary>
    /// <para>过滤空文本和已消费的函数审批响应，再转发模型调用。</para>
    /// <para>Filters empty text and consumed function approvals before forwarding the model call.</para>
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
    ) =>
        base.GetResponseAsync(
            FilterMessages(messages),
            ExecutionRunOptions.WithoutExecution(options),
            cancellationToken
        );

    /// <summary>
    /// <para>过滤空文本和已消费的函数审批响应，再转发模型调用。</para>
    /// <para>Filters empty text and consumed function approvals before forwarding the model call.</para>
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
    ) =>
        base.GetStreamingResponseAsync(
            FilterMessages(messages),
            ExecutionRunOptions.WithoutExecution(options),
            cancellationToken
        );

    /// <summary>
    /// <para>移除不适合模型输入的内容及因此变空的消息，仅在内容变化时复制消息。</para>
    /// <para>Removes unsupported model-input content and resulting empty messages, cloning messages only when content changes.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <returns>
    /// <para>不含被过滤内容及空消息的模型输入列表。</para>
    /// <para>Model-input messages without filtered content or resulting empty messages.</para>
    /// </returns>
    private static List<ChatMessage> FilterMessages(IEnumerable<ChatMessage> messages)
    {
        var filteredMessages = new List<ChatMessage>();
        foreach (var message in messages)
        {
            var contents = message.Contents.Where(IsModelInputContent).ToList();
            if (contents.Count == 0)
            {
                continue;
            }

            if (contents.Count == message.Contents.Count)
            {
                filteredMessages.Add(message);
                continue;
            }

            // 仅在过滤改变内容时复制，既保留原消息，又避免无变化请求的多余复制。
            // Clone only when filtering changes content, preserving the original without copying unchanged messages.
            var filteredMessage = message.Clone();
            filteredMessage.Contents = contents;
            filteredMessages.Add(filteredMessage);
        }

        return filteredMessages;
    }

    /// <summary>
    /// <para>判断内容是否可继续发送给模型：排除空文本和函数审批响应，保留 MCP 审批等其他内容。</para>
    /// <para>Determines model-input eligibility, excluding empty text and function approvals while retaining MCP approvals and other content.</para>
    /// </summary>
    /// <param name="content">
    /// <para>单个待检查或转换的 AI 内容项。</para>
    /// <para>Individual AI content item to inspect or transform.</para>
    /// </param>
    /// <returns>
    /// <para>内容应保留在模型输入中时为 true。</para>
    /// <para>True when the content should remain in model input.</para>
    /// </returns>
    private static bool IsModelInputContent(AIContent content) =>
        content switch
        {
            TextContent text => !string.IsNullOrEmpty(text.Text),
            // 函数审批已由 FunctionInvokingChatClient 消费；MCP 审批仍需交给 Provider，不能一并过滤。
            // Function approvals are consumed by FunctionInvokingChatClient; MCP approvals remain provider input.
            ToolApprovalResponseContent { ToolCall: FunctionCallContent } => false,
            _ => true,
        };
}
