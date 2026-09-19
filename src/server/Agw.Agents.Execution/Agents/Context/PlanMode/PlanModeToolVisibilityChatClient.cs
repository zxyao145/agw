using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Context.PlanMode;

/// <summary>
/// <para>从模型请求中隐藏 Plan 模式限制的工具，保留独立的执行权限检查。</para>
/// <para>Hides Plan-restricted tools from model requests while leaving execution-time permission checks in place.</para>
/// </summary>
internal sealed class PlanModeToolVisibilityChatClient : DelegatingChatClient
{
    /// <summary>
    /// <para>创建 PlanModeToolVisibilityChatClient 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes PlanModeToolVisibilityChatClient with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerClient">
    /// <para>接收处理后请求的内层聊天客户端。</para>
    /// <para>Inner chat client receiving the processed request.</para>
    /// </param>
    public PlanModeToolVisibilityChatClient(IChatClient innerClient)
        : base(innerClient) { }

    /// <summary>
    /// <para>按需复制调用选项，隐藏受限工具后转发请求。</para>
    /// <para>Clones options when necessary and forwards the request with restricted tools hidden.</para>
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
    ) => base.GetResponseAsync(messages, HideRestrictedTools(options), cancellationToken);

    /// <summary>
    /// <para>按需复制调用选项，隐藏受限工具后转发请求。</para>
    /// <para>Clones options when necessary and forwards the request with restricted tools hidden.</para>
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
    ) => base.GetStreamingResponseAsync(messages, HideRestrictedTools(options), cancellationToken);

    /// <summary>
    /// <para>存在隐藏工具时复制选项并过滤工具列表；没有变化时返回原选项。</para>
    /// <para>Clones options and filters the tool list when hidden tools exist, returning the original options when unchanged.</para>
    /// </summary>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <returns>
    /// <para>过滤后的选项副本，或无需变化时的原选项（包括空值）。</para>
    /// <para>Filtered options copy, or unchanged original options, including null.</para>
    /// </returns>
    private static ChatOptions? HideRestrictedTools(ChatOptions? options)
    {
        if (
            options?.Tools == null
            || !options.Tools.Any(static tool => tool is PlanModeRestrictedAIFunction { HideFromModel: true })
        )
        {
            return options;
        }

        // 在副本上过滤可见工具；调用者复用的选项和独立执行权限检查保持不变。
        // Filter visible tools on a copy, preserving caller-owned options and independent execution checks.
        var filteredOptions = options.Clone();
        filteredOptions.Tools = options
            .Tools.Where(static tool => tool is not PlanModeRestrictedAIFunction { HideFromModel: true })
            .ToArray();
        return filteredOptions;
    }
}
