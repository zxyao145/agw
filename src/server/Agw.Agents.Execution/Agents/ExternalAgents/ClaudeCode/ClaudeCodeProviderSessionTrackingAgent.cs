using System.Runtime.CompilerServices;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;

/// <summary>
/// <para>从 Claude Code 初始化消息中捕获 Provider 会话 ID，并过滤传输通知。</para>
/// <para>Captures the provider session ID from Claude Code initialization messages and filters transport notifications.</para>
/// </summary>
/// <remarks>
/// <para>每个包装器仅领取一次有效会话 ID 的通知权；回调使用不可取消令牌。领取后即使回调失败，也不会自动重试。</para>
/// <para>Claims notification of a valid session ID only once per wrapper and uses a non-cancellable callback. Once claimed, a failed callback is not retried automatically.</para>
/// </remarks>
internal sealed class ClaudeCodeProviderSessionTrackingAgent : DelegatingAIAgent
{
    private readonly Func<string, CancellationToken, ValueTask>? _onProviderSessionStartedAsync;
    private int _providerSessionCaptured;

    /// <summary>
    /// <para>创建 ClaudeCodeProviderSessionTrackingAgent 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes ClaudeCodeProviderSessionTrackingAgent with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="onProviderSessionStartedAsync">
    /// <para>收到首个有效 Provider 会话 ID 时调用的持久化回调。</para>
    /// <para>Persistence callback invoked for the first valid provider session ID.</para>
    /// </param>
    public ClaudeCodeProviderSessionTrackingAgent(
        AIAgent innerAgent,
        Func<string, CancellationToken, ValueTask>? onProviderSessionStartedAsync
    )
        : base(innerAgent)
    {
        _onProviderSessionStartedAsync = onProviderSessionStartedAsync;
    }

    /// <summary>
    /// <para>转发内层执行并检查初始化内容，发现首个有效会话 ID 时通知持久化回调。</para>
    /// <para>Forwards inner execution, inspecting initialization content and notifying the persistence callback for the first valid session ID.</para>
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
        var response = await InnerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
        {
            await CaptureProviderSessionIdAsync(message.AdditionalProperties).ConfigureAwait(false);
        }

        response.Messages = response
            .Messages.Where(message =>
                !ClaudeCodeMessagePolicy.IsTransportEvent(message.Role, message.AdditionalProperties, message.Contents)
            )
            .ToList();
        return response;
    }

    /// <summary>
    /// <para>转发内层执行并检查初始化内容，发现首个有效会话 ID 时通知持久化回调。</para>
    /// <para>Forwards inner execution, inspecting initialization content and notifying the persistence callback for the first valid session ID.</para>
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
        await foreach (
            var update in InnerAgent
                .RunStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            await CaptureProviderSessionIdAsync(update.AdditionalProperties).ConfigureAwait(false);
            if (!ClaudeCodeMessagePolicy.IsTransportEvent(update.Role, update.AdditionalProperties, update.Contents))
            {
                yield return update;
            }
        }
    }

    /// <summary>
    /// <para>识别有效初始化会话 ID，原子领取通知权后以不可取消令牌执行回调。</para>
    /// <para>Recognizes a valid initialization session ID, atomically claims notification, and invokes the callback with a non-cancellable token.</para>
    /// </summary>
    /// <param name="additionalProperties">
    /// <para>响应附加属性，包含初始化消息的 subtype 标记。</para>
    /// <para>Response properties containing the initialization subtype marker.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private async ValueTask CaptureProviderSessionIdAsync(AdditionalPropertiesDictionary? additionalProperties)
    {
        if (
            _onProviderSessionStartedAsync == null
            || Volatile.Read(ref _providerSessionCaptured) != 0
            || !TryGetProviderSessionId(additionalProperties, out var providerSessionId)
            || Interlocked.CompareExchange(ref _providerSessionCaptured, 1, 0) != 0
        )
        {
            return;
        }

        // 通知权已原子领取，使用不可取消令牌完成会话绑定；失败不会在本实例重试。
        // Notification is already atomically claimed; bind the session with a non-cancellable token, without retrying on this instance.
        await _onProviderSessionStartedAsync(providerSessionId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>从 init 元数据读取会话 ID，并规范化非空 GUID。</para>
    /// <para>Reads and normalizes a nonempty GUID from init metadata.</para>
    /// </summary>
    /// <param name="additionalProperties">
    /// <para>响应附加属性，包含初始化消息的 subtype 标记。</para>
    /// <para>Response properties containing the initialization subtype marker.</para>
    /// </param>
    /// <param name="providerSessionId">
    /// <para>成功时接收规范化会话 ID，否则为空字符串。</para>
    /// <para>Receives the normalized session ID on success, otherwise an empty string.</para>
    /// </param>
    /// <returns>
    /// <para>成功提取有效非空 GUID 会话 ID 时为 true。</para>
    /// <para>True when a valid nonempty GUID session ID is extracted.</para>
    /// </returns>
    private static bool TryGetProviderSessionId(
        AdditionalPropertiesDictionary? additionalProperties,
        out string providerSessionId
    )
    {
        providerSessionId = string.Empty;
        if (
            additionalProperties?.TryGetValue("subtype", out var subtype) != true
            || !string.Equals(subtype?.ToString(), "init", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        if (additionalProperties.TryGetValue("session_id", out var metadataSessionId))
        {
            if (!Guid.TryParse(metadataSessionId?.ToString(), out var parsedId) || parsedId == Guid.Empty)
            {
                return false;
            }
            providerSessionId = parsedId.Normalize();
            return true;
        }

        return false;
    }
}
