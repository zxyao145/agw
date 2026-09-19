using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Tools.Impl.ToolBlocks.Todo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.ToolFeedback;

/// <summary>
/// <para>统一输出工具初始化告警、调用告警以及 Todo 和 Mode 状态快照。</para>
/// <para>Emits tool setup warnings, invocation warnings, and Todo and Mode state snapshots.</para>
/// </summary>
/// <remarks>
/// <para>初始化告警在最前，调用告警在对应结果前，状态快照在结果后；混合结果批次先发 Todo 再发 Mode。启用 Todo 时普通执行聚合流式结果，保留逐次状态变化。</para>
/// <para>Setup warnings come first, invocation warnings precede their results, and snapshots follow results. Mixed batches emit Todo before Mode. With Todo enabled, non-streaming calls aggregate streaming output to preserve intermediate state changes.</para>
/// </remarks>
internal sealed class ToolFeedbackMiddleware
{
    private static readonly IReadOnlySet<string> TodoToolNames = new HashSet<string>(
        ["todos_add", "todos_complete", "todos_remove", "todos_get_remaining", "todos_get_all"],
        StringComparer.OrdinalIgnoreCase
    );

    private readonly AgwTodoProvider? _todoProvider;
    private readonly AgentModeProvider? _modeProvider;
    private readonly IReadOnlyList<string> _warnings;
    private readonly IReadOnlyDictionary<string, string> _invocationWarnings;

    /// <summary>
    /// <para>创建 ToolFeedbackMiddleware 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes ToolFeedbackMiddleware with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="todoProvider">
    /// <para>读取 Todo 会话状态的 Provider；为空时禁用 Todo 快照。</para>
    /// <para>Provider reading Todo session state; null disables Todo snapshots.</para>
    /// </param>
    /// <param name="modeProvider">
    /// <para>读取当前模式的 Provider；为空时禁用 Mode 快照。</para>
    /// <para>Provider reading the current mode; null disables Mode snapshots.</para>
    /// </param>
    /// <param name="warnings">
    /// <para>在响应开头发送的工具初始化告警，可为空集合。</para>
    /// <para>Tool setup warnings emitted at response start; may be empty.</para>
    /// </param>
    /// <param name="invocationWarnings">
    /// <para>按工具名索引、仅在对应调用返回结果时发送的告警。</para>
    /// <para>Warnings keyed by tool name and emitted only when the corresponding call returns a result.</para>
    /// </param>
    public ToolFeedbackMiddleware(
        AgwTodoProvider? todoProvider,
        AgentModeProvider? modeProvider,
        IReadOnlyList<string> warnings,
        IReadOnlyDictionary<string, string> invocationWarnings
    )
    {
        _todoProvider = todoProvider;
        _modeProvider = modeProvider;
        _warnings = warnings;
        _invocationWarnings = invocationWarnings;
    }

    /// <summary>
    /// <para>执行普通调用并插入工具反馈；启用 Todo 时改为聚合流式执行，以保留每次工具变更的快照。</para>
    /// <para>Executes a non-streaming call with tool feedback, aggregating streaming execution when Todo is enabled to preserve per-mutation snapshots.</para>
    /// </summary>
    /// <remarks>
    /// <para>没有 Todo Provider 时直接执行普通调用并仅补 Mode 快照；缺少会话时仍输出告警，但不读取会话快照。</para>
    /// <para>Without a Todo provider, executes a normal call and adds only Mode snapshots. Missing sessions still allow warnings but disable session snapshots.</para>
    /// </remarks>
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
    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        // 保留原 Todo 中间件的流式聚合路径，让普通执行也能捕获每次变更的状态，而非仅最终列表。
        // The former streaming-only Todo middleware made the SDK aggregate streaming for RunAsync.
        // Keep that route so each mutation captures its own state instead of only the final list.
        if (_todoProvider != null)
        {
            return await RunStreamingAsync(messages, session, options, innerAgent, cancellationToken)
                .ToAgentResponseAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var inputMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        // 每次执行新建调用映射和去重集合；不能与下一回合或其他会话共享。
        // Create fresh call mappings and deduplication sets for each run, never sharing them across turns or sessions.
        var state = new FeedbackState();
        RegisterCalls(inputMessages.SelectMany(static message => message.Contents), state, includeWarnings: false);
        var prelude = CreatePrelude(session);
        try
        {
            var response = await innerAgent
                .RunAsync(inputMessages, session, options, cancellationToken)
                .ConfigureAwait(false);
            var rewritten = new List<ChatMessage>(prelude);
            foreach (var message in response.Messages)
            {
                RegisterCalls(message.Contents, state, includeWarnings: true);
                rewritten.AddRange(CreateInvocationWarnings(message.Contents, state));
                rewritten.Add(message);
                rewritten.AddRange(await CreateModeSnapshotsAsync(message.Contents, session, state, cancellationToken));
            }

            if (rewritten.Count != response.Messages.Count)
            {
                response.Messages.Clear();
                foreach (var message in rewritten)
                    response.Messages.Add(message);
            }
            return response;
        }
        finally
        {
            // 只清理由本次初始化告警建立的历史前置状态。
            // Clear only history-prelude state established by this run's setup warnings.
            if (prelude.Count > 0)
                ConversationHistoryPrelude.Clear(session);
        }
    }

    /// <summary>
    /// <para>依次输出初始化告警、调用告警、原始更新和状态快照，并在退出时清理历史前置消息。</para>
    /// <para>Emits setup warnings, invocation warnings, original updates, and state snapshots in order, clearing history prelude state on exit.</para>
    /// </summary>
    /// <remarks>
    /// <para>输入和审批携带的调用只参与快照识别；调用告警只跟踪输出中的直接调用。各类去重集合不跨执行共享。</para>
    /// <para>Input and approval-carried calls participate only in snapshot recognition; invocation warnings track direct output calls only. Deduplication sets are never shared across runs.</para>
    /// </remarks>
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
    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var inputMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        // 每次执行新建调用映射和去重集合；不能与下一回合或其他会话共享。
        // Create fresh call mappings and deduplication sets for each run, never sharing them across turns or sessions.
        var state = new FeedbackState();
        RegisterCalls(inputMessages.SelectMany(static message => message.Contents), state, includeWarnings: false);
        var prelude = CreatePrelude(session);
        try
        {
            foreach (var message in prelude)
                yield return ToolStateSnapshots.ToUpdate(message);

            await foreach (
                var update in innerAgent
                    .RunStreamingAsync(inputMessages, session, options, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                RegisterCalls(update.Contents, state, includeWarnings: true);
                foreach (var warning in CreateInvocationWarnings(update.Contents, state))
                    yield return ToolStateSnapshots.ToUpdate(warning);

                // 调用告警已输出；先转发原结果，再读取会话中的 Todo 和 Mode 状态。
                // Invocation warnings are already emitted; forward the result before reading Todo and Mode session state.
                yield return update;

                // Todo 快照只在流式路径生成；同批包含两类结果时，保持 Todo 在 Mode 之前。
                // Todo feedback is streaming-only; preserve Todo-before-Mode for a mixed result batch.
                if (session != null && _todoProvider != null)
                {
                    foreach (var result in update.Contents.OfType<FunctionResultContent>())
                    {
                        if (
                            !IsSuccessfulResult(result, state, out var toolName)
                            || !TodoToolNames.Contains(toolName)
                            || !state.TodoCallIds.Add(result.CallId)
                        )
                            continue;

                        var snapshot = await ToolStateSnapshots.CreateTodoAsync(
                            _todoProvider,
                            session,
                            toolName,
                            result.CallId,
                            cancellationToken
                        );
                        yield return ToolStateSnapshots.ToUpdate(snapshot);
                    }
                }

                foreach (
                    var snapshot in await CreateModeSnapshotsAsync(update.Contents, session, state, cancellationToken)
                )
                    yield return ToolStateSnapshots.ToUpdate(snapshot);
            }
        }
        finally
        {
            // 只清理由本次初始化告警建立的历史前置状态。
            // Clear only history-prelude state established by this run's setup warnings.
            if (prelude.Count > 0)
                ConversationHistoryPrelude.Clear(session);
        }
    }

    /// <summary>
    /// <para>创建初始化告警消息，并仅在非空时暂存为当前会话的历史前置消息。</para>
    /// <para>Creates setup-warning messages and stages them as the session history prelude only when nonempty.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话；可以为空，相关状态处理会按方法规则跳过或委托内层。</para>
    /// <para>Current SDK session; may be null, with state handling skipped or delegated according to the method contract.</para>
    /// </param>
    /// <returns>
    /// <para>新建的初始化告警消息列表。</para>
    /// <para>Newly created setup-warning messages.</para>
    /// </returns>
    private List<ChatMessage> CreatePrelude(AgentSession? session)
    {
        var messages = _warnings.Select(CreateWarning).ToList();
        if (messages.Count > 0)
            ConversationHistoryPrelude.Set(session, messages);
        return messages;
    }

    /// <summary>
    /// <para>针对已返回结果且已知名称的调用生成告警，每个 CallId 最多检查并提示一次。</para>
    /// <para>Generates warnings for completed calls with known names, checking and warning at most once per CallId.</para>
    /// </summary>
    /// <param name="contents">
    /// <para>按原始顺序提供的消息内容项。</para>
    /// <para>Message content items in their original order.</para>
    /// </param>
    /// <param name="state">
    /// <para>当前执行独享的调用映射和反馈去重状态。</para>
    /// <para>Call mappings and feedback-deduplication state owned by this run.</para>
    /// </param>
    /// <returns>
    /// <para>本批结果首次触发的调用告警消息。</para>
    /// <para>Invocation-warning messages first triggered by this result batch.</para>
    /// </returns>
    private IEnumerable<ChatMessage> CreateInvocationWarnings(IEnumerable<AIContent> contents, FeedbackState state)
    {
        foreach (var result in contents.OfType<FunctionResultContent>())
        {
            if (
                !state.WarnedCallIds.Add(result.CallId)
                || !state.WarningCallNames.TryGetValue(result.CallId, out var toolName)
                || !_invocationWarnings.TryGetValue(toolName, out var warning)
            )
                continue;

            var message = CreateWarning(warning);
            message.AdditionalProperties!["toolName"] = toolName;
            message.AdditionalProperties["callId"] = result.CallId;
            message.AdditionalProperties["persistSeparately"] = true;
            yield return message;
        }
    }

    /// <summary>
    /// <para>为成功的 mode_set 结果读取会话当前模式，并按 CallId 去重生成快照。</para>
    /// <para>Reads the current session mode for successful mode_set results and emits snapshots deduplicated by CallId.</para>
    /// </summary>
    /// <param name="contents">
    /// <para>按原始顺序提供的消息内容项。</para>
    /// <para>Message content items in their original order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；可以为空，相关状态处理会按方法规则跳过或委托内层。</para>
    /// <para>Current SDK session; may be null, with state handling skipped or delegated according to the method contract.</para>
    /// </param>
    /// <param name="state">
    /// <para>当前执行独享的调用映射和反馈去重状态。</para>
    /// <para>Call mappings and feedback-deduplication state owned by this run.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>本批成功模式变更的快照；无会话、Provider 或匹配结果时为空集合。</para>
    /// <para>Snapshots for successful mode changes in this batch, or an empty collection when session, provider, or matching results are absent.</para>
    /// </returns>
    private async ValueTask<IReadOnlyList<ChatMessage>> CreateModeSnapshotsAsync(
        IEnumerable<AIContent> contents,
        AgentSession? session,
        FeedbackState state,
        CancellationToken cancellationToken
    )
    {
        if (session == null || _modeProvider == null)
            return [];

        var snapshots = new List<ChatMessage>();
        foreach (var result in contents.OfType<FunctionResultContent>())
        {
            if (
                !IsSuccessfulResult(result, state, out var toolName)
                || !string.Equals(toolName, "mode_set", StringComparison.OrdinalIgnoreCase)
                || !state.ModeCallIds.Add(result.CallId)
            )
                continue;

            snapshots.Add(
                await ToolStateSnapshots.CreateModeAsync(
                    _modeProvider,
                    session,
                    toolName,
                    result.CallId,
                    cancellationToken
                )
            );
        }
        return snapshots;
    }

    /// <summary>
    /// <para>检查结果没有异常或工具错误标记，并且能找到已登记调用的工具名。</para>
    /// <para>Checks that a result has no exception or tool-error marker and resolves to a registered call name.</para>
    /// </summary>
    /// <param name="result">
    /// <para>待检查的函数调用结果。</para>
    /// <para>Function-call result to inspect.</para>
    /// </param>
    /// <param name="state">
    /// <para>当前执行独享的调用映射和反馈去重状态。</para>
    /// <para>Call mappings and feedback-deduplication state owned by this run.</para>
    /// </param>
    /// <param name="toolName">
    /// <para>成功时返回匹配的工具名；失败时调用方不应使用该值。</para>
    /// <para>Receives the matched tool name on success; callers must ignore it on failure.</para>
    /// </param>
    /// <returns>
    /// <para>结果未标记为失败且工具名已找到时为 true；具体工具是否需要快照由调用方判断。</para>
    /// <para>True when the result has no failure marker and its tool name is resolved; the caller decides whether that tool needs a snapshot.</para>
    /// </returns>
    private static bool IsSuccessfulResult(FunctionResultContent result, FeedbackState state, out string toolName)
    {
        toolName = string.Empty;
        return result.Exception == null
            && !ToolInvocationExceptionHandler.IsErrorResult(result.Result)
            && state.SnapshotCallNames.TryGetValue(result.CallId, out toolName!);
    }

    /// <summary>
    /// <para>分别登记输出中的直接调用及快照可识别的审批调用，保留两类反馈不同的识别规则。</para>
    /// <para>Registers direct output calls separately from approval-carried snapshot calls, preserving each feedback category's recognition rules.</para>
    /// </summary>
    /// <param name="contents">
    /// <para>按原始顺序提供的消息内容项。</para>
    /// <para>Message content items in their original order.</para>
    /// </param>
    /// <param name="state">
    /// <para>当前执行独享的调用映射和反馈去重状态。</para>
    /// <para>Call mappings and feedback-deduplication state owned by this run.</para>
    /// </param>
    /// <param name="includeWarnings">
    /// <para>是否同时登记直接输出调用；处理原始输入时为 false。</para>
    /// <para>Whether to also register direct output calls; false when processing original input.</para>
    /// </param>
    private static void RegisterCalls(IEnumerable<AIContent> contents, FeedbackState state, bool includeWarnings)
    {
        foreach (var content in contents)
        {
            // 调用告警只跟踪输出里的直接函数调用，包括 informational 调用。
            // Invocation warnings track only direct calls in output, including informational calls.
            if (
                includeWarnings
                && content is FunctionCallContent directCall
                && !string.IsNullOrWhiteSpace(directCall.CallId)
                && !string.IsNullOrWhiteSpace(directCall.Name)
            )
                state.WarningCallNames[directCall.CallId] = directCall.Name;

            // 快照还识别原始输入和审批继续执行所携带的调用，并排除 informational 调用。
            // Snapshots also recognize calls carried by approval continuations and original input.
            var call = content switch
            {
                FunctionCallContent functionCall => functionCall,
                ToolApprovalRequestContent { ToolCall: FunctionCallContent functionCall } => functionCall,
                ToolApprovalResponseContent { ToolCall: FunctionCallContent functionCall } => functionCall,
                AlwaysApproveToolApprovalResponseContent { InnerResponse.ToolCall: FunctionCallContent functionCall } =>
                    functionCall,
                _ => null,
            };
            if (
                call != null
                && !call.InformationalOnly
                && !string.IsNullOrWhiteSpace(call.CallId)
                && !string.IsNullOrWhiteSpace(call.Name)
            )
                state.SnapshotCallNames[call.CallId] = call.Name;
        }
    }

    /// <summary>
    /// <para>创建具有唯一消息 ID 和工具告警类型标记的 System 消息。</para>
    /// <para>Creates a system message with a unique message ID and the tool-warning type marker.</para>
    /// </summary>
    /// <param name="warning">
    /// <para>发送给客户端的告警文本。</para>
    /// <para>Warning text sent to the client.</para>
    /// </param>
    /// <returns>
    /// <para>已标记为工具告警的 System 消息。</para>
    /// <para>System message marked as a tool warning.</para>
    /// </returns>
    private static ChatMessage CreateWarning(string warning) =>
        new(ChatRole.System, [new TextContent(warning)])
        {
            MessageId = Guid.CreateVersion7().ToString("N"),
            AuthorName = "tools",
            AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = ToolMessageTypes.Warning },
        };

    /// <summary>
    /// <para>保存单次执行的工具调用映射及各类反馈的独立去重集合。</para>
    /// <para>Stores per-run tool-call mappings and independent deduplication sets for each feedback category.</para>
    /// </summary>
    private sealed class FeedbackState
    {
        /// <summary>
        /// <para>输出中直接函数调用的 CallId 到工具名映射。</para>
        /// <para>Maps direct output function-call IDs to tool names.</para>
        /// </summary>
        public Dictionary<string, string> WarningCallNames { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// <para>原始输入和输出中可参与快照的调用映射，包含审批携带的调用。</para>
        /// <para>Maps snapshot-eligible calls from original input and output, including calls carried by approvals.</para>
        /// </summary>
        public Dictionary<string, string> SnapshotCallNames { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// <para>已检查调用告警的结果 ID，即使未找到告警也不重复检查。</para>
        /// <para>Result IDs already checked for invocation warnings, including checks that found no warning.</para>
        /// </summary>
        public HashSet<string> WarnedCallIds { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// <para>已生成 Todo 快照的成功调用 ID。</para>
        /// <para>Successful call IDs for which Todo snapshots have been generated.</para>
        /// </summary>
        public HashSet<string> TodoCallIds { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// <para>已生成 Mode 快照的成功调用 ID。</para>
        /// <para>Successful call IDs for which Mode snapshots have been generated.</para>
        /// </summary>
        public HashSet<string> ModeCallIds { get; } = new(StringComparer.Ordinal);
    }
}
