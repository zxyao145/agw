using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

/// <summary>
/// <para>在 SDK 调用边界刷新权限、同步会话审批状态并记录可复用授权。</para>
/// <para>Refreshes permissions, synchronizes session approval state, and records reusable grants at the SDK call boundary.</para>
/// </summary>
/// <remarks>
/// <para>不改写 SDK 审批队列；只持久化带有效授权范围的已批准函数响应。权限同步发生在转发内层调用之前。</para>
/// <para>Does not rewrite the SDK approval queue. Persists only approved function responses with a valid grant scope, synchronizing permissions before forwarding the inner call.</para>
/// </remarks>
internal sealed class MafApprovalGrantAgent : DelegatingAIAgent
{
    private readonly HumanInteractionContextAccessor? _interactions;

    /// <summary>
    /// <para>创建 MafApprovalGrantAgent 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes MafApprovalGrantAgent with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="interactions">
    /// <para>提供最新权限和授权状态的交互访问器；可以为空。</para>
    /// <para>Interaction accessor providing current permissions and grant state; may be null.</para>
    /// </param>
    public MafApprovalGrantAgent(AIAgent innerAgent, HumanInteractionContextAccessor? interactions)
        : base(innerAgent)
    {
        _interactions = interactions;
    }

    /// <summary>
    /// <para>在执行前刷新权限和可复用授权，然后转发原始请求。</para>
    /// <para>Refreshes permissions and reusable grants before forwarding the original request.</para>
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
        var input = messages.ToArray();
        await ApplyAsync(input, session, cancellationToken).ConfigureAwait(false);
        return await InnerAgent.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>在执行前刷新权限和可复用授权，然后转发原始请求。</para>
    /// <para>Refreshes permissions and reusable grants before forwarding the original request.</para>
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
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var input = messages.ToArray();
        await ApplyAsync(input, session, cancellationToken).ConfigureAwait(false);
        await foreach (
            var update in InnerAgent.RunStreamingAsync(input, session, options, cancellationToken).ConfigureAwait(false)
        )
            yield return update;
    }

    /// <summary>
    /// <para>刷新交互权限、同步并应用会话权限模式，再记录带有效范围的已批准函数响应。</para>
    /// <para>Refreshes interaction permissions, synchronizes and applies the session permission mode, then records approved function responses with valid scopes.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；可以为空，相关状态处理会按方法规则跳过或委托内层。</para>
    /// <para>Current SDK session; may be null, with state handling skipped or delegated according to the method contract.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private async ValueTask ApplyAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        CancellationToken cancellationToken
    )
    {
        if (_interactions is not null)
            await _interactions.RefreshPermissionsAsync(cancellationToken).ConfigureAwait(false);
        if (session is null)
            return;
        // 先同步最新权限模式，再应用或记录授权，避免旧模式的授权继续生效。
        // Synchronize the latest permission mode before applying or recording grants so old-mode grants cannot remain effective.
        var mode = _interactions?.PermissionState is { } permissions
            ? MafSessionApprovalState.Synchronize(session, permissions)
            : MafSessionApprovalState.GetPermissionMode(session);
        MafSessionApprovalState.Apply(session, mode);
        // 只从已批准且带有效授权范围的函数响应记录授权，不修改 SDK 审批队列。
        // Record grants only from approved function responses with valid scopes, leaving the SDK approval queue untouched.
        foreach (var response in messages.SelectMany(message => message.Contents).OfType<ToolApprovalResponseContent>())
        {
            if (
                response.Approved
                && response.ToolCall is FunctionCallContent call
                && response.AdditionalProperties?.TryGetValue(MafApprovalAdapter.GrantScopeProperty, out var value)
                    == true
                && Enum.TryParse<ApprovalScope>(value?.ToString(), out var scope)
            )
                MafSessionApprovalState.Record(session, call, scope, mode);
        }
    }
}
