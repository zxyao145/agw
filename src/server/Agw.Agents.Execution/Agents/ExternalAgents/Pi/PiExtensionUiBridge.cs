using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiAgentSdk;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Pi;

/// <summary>
/// <para>将 Pi 的确认、选择和文本输入请求转为当前通道上的用户交互。</para>
/// <para>Maps Pi confirmation, selection, and text-input requests to user interactions on the current channel.</para>
/// </summary>
/// <remarks>
/// <para>同一实例不允许并发绑定；后台或缺少通道时返回取消。只接受与请求类型匹配的响应，select 必须属于原选项集合。</para>
/// <para>Rejects concurrent bindings. Background runs or missing channels return cancellation. Responses must match the request type, and selections must belong to the original option set.</para>
/// </remarks>
internal sealed class PiExtensionUiBridge
{
    private readonly HumanInteractionContextAccessor? _contextAccessor;
    private readonly bool _allowInteraction;
    private IHumanInteractionChannel? _activeChannel;
    private int _isBound;

    /// <summary>
    /// <para>创建 PiExtensionUiBridge 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes PiExtensionUiBridge with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="contextAccessor">
    /// <para>读取当前人机交互通道的访问器；可以为空。</para>
    /// <para>Accessor for the current human-interaction channel; may be null.</para>
    /// </param>
    /// <param name="allowInteraction">
    /// <para>是否允许当前 Agent 绑定交互通道；后台执行通常禁用。</para>
    /// <para>Whether this agent may bind an interaction channel; typically disabled for background execution.</para>
    /// </param>
    public PiExtensionUiBridge(HumanInteractionContextAccessor? contextAccessor, bool allowInteraction)
    {
        _contextAccessor = contextAccessor;
        _allowInteraction = allowInteraction;
    }

    /// <summary>
    /// <para>在当前交互通道绑定期间执行内层 Agent，结束后解除绑定。</para>
    /// <para>Runs the inner agent while bound to the current interaction channel and unbinds afterward.</para>
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
    public async Task<AgentResponse> BindRunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        using var binding = BindCurrentChannel();
        return await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>在整个流式枚举期间保持通道绑定，流结束或释放时解除绑定。</para>
    /// <para>Keeps the channel bound throughout streaming enumeration and unbinds when the stream ends or is disposed.</para>
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
    public async IAsyncEnumerable<AgentResponseUpdate> BindRunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var binding = BindCurrentChannel();
        await foreach (
            var update in innerAgent
                .RunStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return update;
        }
    }

    /// <summary>
    /// <para>转发 Pi UI 请求并校验响应类型；缺少通道、无效选项或用户取消均返回 SDK 取消响应。</para>
    /// <para>Forwards Pi UI requests and validates response types, returning SDK cancellation for missing channels, invalid selections, or user cancellation.</para>
    /// </summary>
    /// <param name="request">
    /// <para>Pi SDK 发出的 UI 请求，包含方法、提示及可选项。</para>
    /// <para>UI request from the Pi SDK, including method, prompt, and available choices.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>保留原请求 ID 的确认、文本或取消响应。</para>
    /// <para>Confirmation, text, or cancellation response retaining the original request ID.</para>
    /// </returns>
    public async ValueTask<PiExtensionUiResponse> HandleAsync(
        PiExtensionUiRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        // 读取当前执行捕获的通道；后台或未绑定执行直接返回取消，不创建人工等待。
        // Read the channel captured for this run; background or unbound execution cancels without creating a human wait.
        var channel = Volatile.Read(ref _activeChannel);
        if (channel == null)
        {
            return PiExtensionUiResponse.Cancel(request.Id);
        }

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                request.Method,
                request.Title,
                request.Message,
                request.Options,
                request.Placeholder,
                request.Prefill,
                request.Timeout,
            }
        );
        var prompt = request.Title ?? request.Message ?? "Pi requires user input to continue.";
        var interaction = new UserInputRequest(request.Method, prompt, payload)
        {
            Source = new InteractionSource { ToolName = "PiExtensionUI", CallId = request.Id },
        };

        try
        {
            var response = await channel.RequestAsync(interaction, cancellationToken).ConfigureAwait(false);
            if (response.Cancelled || !response.ResponseData.HasValue)
            {
                return PiExtensionUiResponse.Cancel(request.Id);
            }

            var responseData = response.ResponseData.Value;
            if (
                string.Equals(request.Method, "confirm", StringComparison.Ordinal)
                && responseData.TryGetProperty("confirmed", out var confirmed)
                && confirmed.ValueKind is JsonValueKind.True or JsonValueKind.False
            )
            {
                return new PiExtensionUiResponse { Id = request.Id, Confirmed = confirmed.GetBoolean() };
            }

            if (responseData.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            {
                // 选择结果必须属于原请求选项；文本输入只接受字符串，不能接受任意 JSON。
                // Selections must belong to the original options; text input accepts strings, not arbitrary JSON.
                var responseValue = value.GetString();
                if (
                    string.Equals(request.Method, "select", StringComparison.Ordinal)
                    && (
                        responseValue == null
                        || request.Options?.Contains(responseValue, StringComparer.Ordinal) != true
                    )
                )
                {
                    return PiExtensionUiResponse.Cancel(request.Id);
                }

                if (request.Method is "select" or "input" or "editor")
                {
                    return new PiExtensionUiResponse { Id = request.Id, Value = responseValue };
                }
            }

            return PiExtensionUiResponse.Cancel(request.Id);
        }
        catch (OperationCanceledException)
        {
            return PiExtensionUiResponse.Cancel(request.Id);
        }
    }

    /// <summary>
    /// <para>拒绝并发绑定，并在允许交互时捕获当前通道供 SDK 回调使用。</para>
    /// <para>Rejects concurrent bindings and captures the current channel for SDK callbacks when interaction is allowed.</para>
    /// </summary>
    /// <returns>
    /// <para>释放时清空通道并允许下一次绑定的句柄。</para>
    /// <para>Handle that clears the channel and permits another binding on disposal.</para>
    /// </returns>
    /// <exception cref="AgwException">
    /// <para>此实例已绑定另一条正在执行的调用。</para>
    /// <para>This instance is already bound to another active run.</para>
    /// </exception>
    private IDisposable BindCurrentChannel()
    {
        // 同一桥接实例只服务一个活动执行，避免响应被路由到其他调用。
        // Allow one active run per bridge to prevent responses from being routed to another call.
        if (Interlocked.CompareExchange(ref _isBound, 1, 0) != 0)
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Concurrent Pi runs cannot share a human interaction bridge."
            );
        }

        Volatile.Write(ref _activeChannel, _allowInteraction ? _contextAccessor?.Current : null);
        return new Binding(this);
    }

    /// <summary>
    /// <para>清空当前通道并释放执行占用，允许下一次执行重新绑定。</para>
    /// <para>Clears the current channel and releases the run claim so a later run can bind again.</para>
    /// </summary>
    private void Unbind()
    {
        Volatile.Write(ref _activeChannel, null);
        Volatile.Write(ref _isBound, 0);
    }

    /// <summary>
    /// <para>在当前 Pi 执行结束时解除通道绑定。</para>
    /// <para>Releases the channel binding when the current Pi run ends.</para>
    /// </summary>
    private sealed class Binding : IDisposable
    {
        private readonly PiExtensionUiBridge _owner;
        private int _disposed;

        /// <summary>
        /// <para>创建 Binding 实例并保存本包装层使用的依赖和配置。</para>
        /// <para>Initializes Binding with the dependencies and configuration used by this wrapper.</para>
        /// </summary>
        /// <param name="owner">
        /// <para>当前作用域或绑定所属的实例。</para>
        /// <para>Instance that owns this scope or binding.</para>
        /// </param>
        public Binding(PiExtensionUiBridge owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// <para>幂等释放所属 Pi 桥接实例的当前绑定。</para>
        /// <para>Idempotently releases the owning Pi bridge's current binding.</para>
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unbind();
            }
        }
    }
}
