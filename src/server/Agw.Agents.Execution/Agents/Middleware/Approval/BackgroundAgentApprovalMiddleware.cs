using System.Runtime.CompilerServices;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.Approval;

/// <summary>
/// <para>拒绝后台 Agent 执行期间产生的新工具审批请求。</para>
/// <para>Rejects new tool approval requests emitted while a background Agent runs.</para>
/// </summary>
/// <remarks>
/// <para>后台 Agent 的执行作用域没有交互通道；流式响应在转发前检查审批内容，遇到审批请求即失败，不等待人工处理。</para>
/// <para>The background Agent's execution scope has no interaction channel. Streaming updates are checked before forwarding; an approval request fails the run instead of waiting for a person.</para>
/// </remarks>
internal sealed class BackgroundAgentApprovalMiddleware
{
    /// <summary>
    /// <para>执行普通调用，并在返回前拒绝响应中的新审批请求。</para>
    /// <para>Executes a non-streaming call and rejects new approval requests before returning.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；可以为空，相关状态处理会按方法规则跳过或委托内层。</para>
    /// <para>Current SDK session; may be null, with state handling skipped or delegated according to the method contract.</para>
    /// </param>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>完成当前包装层处理后的完整响应。</para>
    /// <para>Complete response after processing by this wrapper.</para>
    /// </returns>
    public async Task<AgentResponse> RejectNewApprovalAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        var response = await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        ThrowIfApprovalRequested(response.Messages.SelectMany(static message => message.Contents));
        return response;
    }

    /// <summary>
    /// <para>在每段转发前拒绝新审批请求。</para>
    /// <para>Rejects new approvals before forwarding each update.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；可以为空，相关状态处理会按方法规则跳过或委托内层。</para>
    /// <para>Current SDK session; may be null, with state handling skipped or delegated according to the method contract.</para>
    /// </param>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>经当前包装层处理后按顺序产生的响应更新流。</para>
    /// <para>Ordered response-update stream after processing by this wrapper.</para>
    /// </returns>
    public async IAsyncEnumerable<AgentResponseUpdate> RejectNewApprovalStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        await foreach (
            var update in innerAgent
                .RunStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            // 先检查再转发，避免消费方收到后台执行无法处理的审批请求。
            // Check before forwarding so consumers never receive approvals the background run cannot handle.
            ThrowIfApprovalRequested(update.Contents);
            yield return update;
        }
    }

    /// <summary>
    /// <para>检测审批请求内容，发现后抛出后台执行不支持审批的业务异常。</para>
    /// <para>Detects approval-request content and throws the background-approval execution error when found.</para>
    /// </summary>
    /// <param name="contents">
    /// <para>按原始顺序提供的消息内容项。</para>
    /// <para>Message content items in their original order.</para>
    /// </param>
    /// <exception cref="AgwException">
    /// <para>内容包含工具审批请求，后台执行无法等待人工批准。</para>
    /// <para>Content contains a tool approval request that background execution cannot wait for.</para>
    /// </exception>
    private static void ThrowIfApprovalRequested(IEnumerable<AIContent> contents)
    {
        if (contents.OfType<ToolApprovalRequestContent>().Any())
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "A background agent requested a new tool approval. Background tasks cannot pause for approval."
            );
        }
    }
}
