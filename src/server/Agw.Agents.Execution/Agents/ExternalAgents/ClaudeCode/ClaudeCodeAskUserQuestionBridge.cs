using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Agw.Tools.HumanInteraction;
using Agw.Tools.Impl.Tools.Basic;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;

/// <summary>
/// <para>将 Claude Code 的问答和工具审批回调接入当前 Agw 人机交互通道。</para>
/// <para>Bridges Claude Code question and tool-approval callbacks to the current Agw human-interaction channel.</para>
/// </summary>
/// <remarks>
/// <para>同一实例不允许并发执行。绑定捕获交互通道、来源和授权缓存作用域；FullAccess 可跳过普通工具审批，但问答仍需要用户真实答案。</para>
/// <para>One instance cannot serve concurrent runs. Binding captures the channel, attribution, and approval-cache scope. FullAccess bypasses ordinary tool approval, but questions still require actual user answers.</para>
/// </remarks>
internal sealed class ClaudeCodeAskUserQuestionBridge
{
    private const string ToolName = "AskUserQuestion";
    private const string Prompt = "The agent needs your input to continue.";

    private readonly HumanInteractionContextAccessor? _contextAccessor;
    private readonly bool _allowInteraction;
    private IHumanInteractionChannel? _activeChannel;
    private int _isBound;
    private readonly AgwPermissionMode? _permissionMode;
    private readonly string? _workingDirectory;
    private readonly Guid _agentId;
    private readonly IRuntimeTurnContextAccessor? _turnContext;
    private readonly ClaudeToolApprovalCache _cache;
    private string? _scope;
    private long _version;
    private InteractionSource _source = new();

    /// <summary>
    /// <para>创建 ClaudeCodeAskUserQuestionBridge 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes ClaudeCodeAskUserQuestionBridge with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="contextAccessor">
    /// <para>读取当前人机交互通道的访问器；可以为空。</para>
    /// <para>Accessor for the current human-interaction channel; may be null.</para>
    /// </param>
    /// <param name="allowInteraction">
    /// <para>是否允许当前 Agent 绑定交互通道；后台执行通常禁用。</para>
    /// <para>Whether this agent may bind an interaction channel; typically disabled for background execution.</para>
    /// </param>
    /// <param name="permissionMode">
    /// <para>当前执行使用的权限模式快照。</para>
    /// <para>Permission-mode snapshot used for this execution.</para>
    /// </param>
    /// <param name="workingDirectory">
    /// <para>纳入授权缓存作用域的工作目录。</para>
    /// <para>Working directory included in the approval-cache scope.</para>
    /// </param>
    /// <param name="agentId">
    /// <para>用于会话、授权或跟踪归属的 Agent 标识。</para>
    /// <para>Agent identifier used for session, approval, or trace attribution.</para>
    /// </param>
    /// <param name="turnContext">
    /// <para>提供当前用户、项目、会话和权限版本的回合上下文访问器。</para>
    /// <para>Turn-context accessor supplying user, project, conversation, and permission version.</para>
    /// </param>
    /// <param name="cache">
    /// <para>可复用的同参数授权缓存；为空时为此桥接实例创建缓存。</para>
    /// <para>Reusable same-argument approval cache; null creates a cache for this bridge instance.</para>
    /// </param>
    public ClaudeCodeAskUserQuestionBridge(
        HumanInteractionContextAccessor? contextAccessor,
        bool allowInteraction,
        AgwPermissionMode? permissionMode = null,
        string? workingDirectory = null,
        Guid agentId = default,
        IRuntimeTurnContextAccessor? turnContext = null,
        ClaudeToolApprovalCache? cache = null
    )
    {
        _contextAccessor = contextAccessor;
        _allowInteraction = allowInteraction;
        _permissionMode = permissionMode;
        _workingDirectory = workingDirectory;
        _agentId = agentId;
        _turnContext = turnContext;
        _cache = cache ?? new ClaudeToolApprovalCache();
    }

    /// <summary>
    /// <para>绑定本次执行的交互通道和授权作用域，转发调用，并在结束时解除绑定。</para>
    /// <para>Binds the interaction channel and approval scope for this run, forwards the call, and unbinds on exit.</para>
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
        using var binding = BindCurrentChannel(options);
        return await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>为枚举生命周期绑定交互通道和授权作用域，逐段转发并在释放时解除绑定。</para>
    /// <para>Binds the channel and approval scope for the enumeration lifetime, forwarding updates and unbinding on disposal.</para>
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
        using var binding = BindCurrentChannel(options);
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
    /// <para>将 AskUserQuestion 转为用户输入请求并校验真实答案；其他工具走普通审批流程。</para>
    /// <para>Converts AskUserQuestion into user input and validates actual answers, routing other tools through ordinary approval.</para>
    /// </summary>
    /// <param name="toolName">
    /// <para>工具的协议名称。</para>
    /// <para>Protocol name of the tool.</para>
    /// </param>
    /// <param name="input">
    /// <para>Claude SDK 提供的原始工具参数 JSON。</para>
    /// <para>Original tool-argument JSON supplied by the Claude SDK.</para>
    /// </param>
    /// <param name="context">
    /// <para>Claude SDK 的工具权限上下文，包含原始 tool_use_id。</para>
    /// <para>Claude SDK permission context containing the original tool_use_id.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>允许执行并携带已校验答案的结果，或不打断执行的拒绝结果。</para>
    /// <para>An allow result carrying validated answers, or a non-interrupting denial.</para>
    /// </returns>
    public async ValueTask<PermissionResult> HandleAsync(
        string toolName,
        JsonElement input,
        ToolPermissionContext context,
        CancellationToken cancellationToken
    )
    {
        if (!string.Equals(toolName, ToolName, StringComparison.Ordinal))
        {
            return await HandleToolApprovalAsync(toolName, input, context, cancellationToken).ConfigureAwait(false);
        }

        var channel = Volatile.Read(ref _activeChannel);
        if (channel == null)
        {
            return Deny("AskUserQuestion requires an active interactive channel.");
        }

        try
        {
            // Map native AskUserQuestion to UserInput; FullAccess still requires actual user answers.
            // 将 Claude 原生 AskUserQuestion 转为 UserInput；FullAccess 下仍需要用户提供真实答案。
            // Send only questions, ignore model-supplied answers, and retain tool_use_id as source attribution.
            // 只发送问题，忽略原始 input 中可能携带的模型答案；tool_use_id 保留为来源调用标识。
            // The channel owns interaction IDs and waiting; only validated answers return as SDK updatedInput.
            // channel 负责交互 ID 与等待，校验后的用户答案才会作为 updatedInput 返回 SDK。
            var toolParams =
                JsonUtil.Deserialize<AskUserQuestionToolParams>(input.GetRawText())
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Question arguments are invalid.");
            AskUserQuestionTool.ValidateQuestions(toolParams.Questions);
            var questions = input.GetProperty("questions").Clone();
            var payload = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement> { ["questions"] = questions }
            );
            var request = new UserInputRequest("questions", Prompt, payload)
            {
                Source = new InteractionSource { ToolName = ToolName, CallId = context.ToolUseId },
            };
            var response = await channel.RequestAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Cancelled)
            {
                return Deny("User cancelled the question request without answering.");
            }

            if (!response.ResponseData.HasValue)
            {
                return Deny("Question response data is required.");
            }

            var responseData =
                JsonUtil.Deserialize<AskUserQuestionResponseData>(response.ResponseData.Value.GetRawText())
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Question response data is invalid.");
            var answers = AskUserQuestionTool.ValidateAnswers(
                toolParams.Questions,
                responseData.Answers,
                responseData.Annotations
            );
            var updatedInput = JsonSerializer.SerializeToElement(
                new Dictionary<string, object?> { ["questions"] = questions, ["answers"] = answers }
            );
            return new PermissionResultAllow(updatedInput);
        }
        catch (AgwException exception)
        {
            return Deny(exception.Message);
        }
        catch (JsonException exception)
        {
            return Deny($"Question payload is invalid: {exception.Message}");
        }
    }

    /// <summary>
    /// <para>按当前权限模式检查直接放行、同参数缓存和人工审批，并缓存允许复用的成功授权。</para>
    /// <para>Checks permission-mode bypasses, same-argument cache entries, and human approval, caching successful reusable grants.</para>
    /// </summary>
    /// <param name="toolName">
    /// <para>工具的协议名称。</para>
    /// <para>Protocol name of the tool.</para>
    /// </param>
    /// <param name="input">
    /// <para>Claude SDK 提供的原始工具参数 JSON。</para>
    /// <para>Original tool-argument JSON supplied by the Claude SDK.</para>
    /// </param>
    /// <param name="context">
    /// <para>Claude SDK 的工具权限上下文，包含原始 tool_use_id。</para>
    /// <para>Claude SDK permission context containing the original tool_use_id.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>依据权限、缓存或人工决定产生的允许或拒绝结果。</para>
    /// <para>Allow or deny result based on permissions, cached grants, or a human decision.</para>
    /// </returns>
    private async ValueTask<PermissionResult> HandleToolApprovalAsync(
        string toolName,
        JsonElement input,
        ToolPermissionContext context,
        CancellationToken cancellationToken
    )
    {
        if (Volatile.Read(ref _isBound) == 0)
            return Deny("Tool approval requires an active run.");
        cancellationToken.ThrowIfCancellationRequested();
        // 这里只处理普通工具审批；AskUserQuestion 已在上层分流，不能由 FullAccess 代答。
        // This path handles ordinary approvals only; AskUserQuestion was routed separately and cannot be answered by FullAccess.
        if (_permissionMode == AgwPermissionMode.FullAccess)
            return new PermissionResultAllow();
        var arguments = JsonNode.Parse(input.GetRawText());
        if (
            _permissionMode == AgwPermissionMode.AllowSameArguments
            && _scope != null
            && _cache.Contains(_scope, _version, toolName, arguments)
        )
            return new PermissionResultAllow();
        if (Volatile.Read(ref _activeChannel) is not IInteractionHandler handler)
            return Deny("Tool approval requires an active interactive channel.");
        var request = new ToolApprovalInteraction
        {
            InteractionId = Guid.CreateVersion7().ToString("N"),
            Prompt = $"Allow Claude Code to use {toolName}?",
            Arguments = input.Clone(),
            Source = _source with { ToolName = toolName, CallId = context.ToolUseId },
        };
        var result = await handler.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is not InteractionResolution.Resolved { Response: ToolApprovalDecision { Approved: true } })
            return Deny("Tool execution was denied.");
        if (_permissionMode == AgwPermissionMode.AllowSameArguments && _scope != null)
            _cache.Add(_scope, _version, toolName, arguments);
        return new PermissionResultAllow();
    }

    /// <summary>
    /// <para>原子占用桥接实例，捕获来源、当前回合和权限版本，并绑定允许使用的交互通道。</para>
    /// <para>Atomically claims the bridge, captures attribution, turn context, and permission version, and binds the permitted interaction channel.</para>
    /// </summary>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <returns>
    /// <para>释放时解除当前执行绑定的句柄。</para>
    /// <para>Handle that unbinds the current run on disposal.</para>
    /// </returns>
    /// <exception cref="AgwException">
    /// <para>此实例已绑定另一条正在执行的调用。</para>
    /// <para>This instance is already bound to another active run.</para>
    /// </exception>
    private IDisposable BindCurrentChannel(AgentRunOptions? options)
    {
        // 在捕获活动通道前占用桥接实例，防止并发 SDK 回调关联到错误回合。
        // Claim the bridge before capturing the channel so concurrent SDK callbacks cannot bind to the wrong turn.
        if (Interlocked.CompareExchange(ref _isBound, 1, 0) != 0)
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Concurrent Claude Code runs cannot share a human interaction bridge."
            );
        }

        _source =
            options?.AdditionalProperties?.TryGetValue(HumanInteractionToolMetadata.SourceKey, out var source) == true
            && source is InteractionSource attribution
                ? attribution
                : new();
        var turn = _turnContext?.Current;
        // 缓存作用域绑定用户、项目、会话代次、Agent、节点和工作区，避免授权跨边界复用。
        // Scope cached grants to user, project, conversation generation, agent, node, and workspace.
        _scope =
            turn == null
                ? null
                : JsonSerializer.Serialize(
                    new
                    {
                        turn.UserId,
                        turn.ProjectId,
                        turn.ProjectConversationId,
                        turn.Task.Generation,
                        AgentId = _agentId,
                        _source.NodeId,
                        Workspace = _workingDirectory,
                    }
                );
        _version = turn?.Settings.PermissionVersion ?? 0;
        Volatile.Write(ref _activeChannel, _allowInteraction ? _contextAccessor?.Current : null);
        return new Binding(this);
    }

    /// <summary>
    /// <para>先清空活动通道，再解除执行占用，避免新执行读取旧通道。</para>
    /// <para>Clears the active channel before releasing the run claim so a new run cannot observe the old channel.</para>
    /// </summary>
    private void Unbind()
    {
        Volatile.Write(ref _activeChannel, null);
        Volatile.Write(ref _isBound, 0);
    }

    /// <summary>
    /// <para>创建拒绝工具调用但不要求 SDK 中断整个执行的结果。</para>
    /// <para>Creates a tool denial that does not request interruption of the entire SDK run.</para>
    /// </summary>
    /// <param name="message">
    /// <para>待检查、复制或补充元数据的消息。</para>
    /// <para>Message to inspect, copy, or annotate.</para>
    /// </param>
    /// <returns>
    /// <para>Interrupt 为 false 的拒绝结果。</para>
    /// <para>Denial result whose Interrupt flag is false.</para>
    /// </returns>
    private static PermissionResultDeny Deny(string message) => new(message, Interrupt: false);

    /// <summary>
    /// <para>在当前 Claude Code 执行结束时解除通道绑定。</para>
    /// <para>Releases the channel binding when the current Claude Code run ends.</para>
    /// </summary>
    private sealed class Binding : IDisposable
    {
        private readonly ClaudeCodeAskUserQuestionBridge _owner;
        private int _disposed;

        /// <summary>
        /// <para>创建 Binding 实例并保存本包装层使用的依赖和配置。</para>
        /// <para>Initializes Binding with the dependencies and configuration used by this wrapper.</para>
        /// </summary>
        /// <param name="owner">
        /// <para>当前作用域或绑定所属的实例。</para>
        /// <para>Instance that owns this scope or binding.</para>
        /// </param>
        public Binding(ClaudeCodeAskUserQuestionBridge owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// <para>幂等解除所属桥接实例的当前通道绑定。</para>
        /// <para>Idempotently releases the owning bridge's current channel binding.</para>
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unbind();
            }
        }
    }

    /// <summary>
    /// <para>承载用户提交的答案及注解，供问题规则校验使用。</para>
    /// <para>Carries user-submitted answers and annotations for question validation.</para>
    /// </summary>
    private sealed class AskUserQuestionResponseData
    {
        /// <summary>
        /// <para>用户提交的与各问题关联的答案字典，缺失时交由校验器处理。</para>
        /// <para>User-submitted answers associated with the questions, with missing values handled by validation.</para>
        /// </summary>
        public Dictionary<string, string>? Answers { get; set; }

        /// <summary>
        /// <para>与用户答案关联的可选注解。</para>
        /// <para>Optional annotations associated with user answers.</para>
        /// </summary>
        public Dictionary<string, AskUserQuestionAnnotation>? Annotations { get; set; }
    }
}
