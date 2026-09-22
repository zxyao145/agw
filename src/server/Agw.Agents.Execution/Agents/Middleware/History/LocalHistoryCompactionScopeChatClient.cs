using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Middleware.History;

/// <summary>
/// <para>为本地持久化历史建立临时压缩上下文，避免 SDK 把本地会话误判为远程托管历史。</para>
/// <para>Creates a temporary compaction context so the SDK does not mistake locally persisted history for provider-managed history.</para>
/// </summary>
/// <remarks>
/// <para>复制输入消息、校验压缩索引版本，并在 finally 中恢复原运行上下文；仅本地历史哨兵会话需要隐藏 SDK 会话服务。</para>
/// <para>Clones inputs, validates the compaction-index version, and restores the original run context in finally. Only local-history sentinel sessions need the SDK session service hidden.</para>
/// </remarks>
internal sealed class LocalHistoryCompactionScopeChatClient : DelegatingChatClient
{
    private const string CompactionIndexVersion = "tool-result-eviction-v5";

    private readonly string? _compactionStateKey;
    private readonly string? _compactionVersionStateKey;
    private readonly ILogger<LocalHistoryCompactionScopeChatClient> _logger;

    /// <summary>
    /// <para>创建 LocalHistoryCompactionScopeChatClient 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes LocalHistoryCompactionScopeChatClient with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerClient">
    /// <para>接收处理后请求的内层聊天客户端。</para>
    /// <para>Inner chat client receiving the processed request.</para>
    /// </param>
    /// <param name="compactionStateKey">
    /// <para>压缩 Provider 状态键；为空白时跳过版本重置。</para>
    /// <para>Compaction-provider state key; blank values skip version resets.</para>
    /// </param>
    /// <param name="logger">
    /// <para>记录执行、兼容处理或清理错误的日志器。</para>
    /// <para>Logger for execution, compatibility handling, or cleanup errors.</para>
    /// </param>
    public LocalHistoryCompactionScopeChatClient(
        IChatClient innerClient,
        string? compactionStateKey,
        ILogger<LocalHistoryCompactionScopeChatClient> logger
    )
        : base(innerClient)
    {
        _compactionStateKey = compactionStateKey;
        _compactionVersionStateKey = string.IsNullOrWhiteSpace(compactionStateKey)
            ? null
            : $"{compactionStateKey}.agw-index-version";
        _logger = logger;
    }

    /// <summary>
    /// <para>隔离输入并临时替换本地历史的压缩上下文，调用结束后恢复原上下文。</para>
    /// <para>Isolates input and temporarily substitutes a local-history compaction context, restoring the original afterward.</para>
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
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var isolatedMessages = ChatMessageSourceIsolation.CloneMessages(messages);
        var originalContext = AIAgent.CurrentRunContext;
        EnsureCompatibleCompactionState(originalContext?.Session);
        var compactionContext = CreateCompactionContext(originalContext, options);
        if (compactionContext == null)
        {
            return await base.GetResponseAsync(isolatedMessages, options, cancellationToken).ConfigureAwait(false);
        }

        RunContextAccessor.SetCurrent(compactionContext);
        try
        {
            return await base.GetResponseAsync(isolatedMessages, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // 恢复进入本包装器前的上下文，防止后续调用看到临时会话。
            // Restore the incoming context so later calls cannot observe the temporary session.
            RunContextAccessor.SetCurrent(originalContext);
        }
    }

    /// <summary>
    /// <para>隔离输入并临时替换本地历史的压缩上下文，调用结束后恢复原上下文。</para>
    /// <para>Isolates input and temporarily substitutes a local-history compaction context, restoring the original afterward.</para>
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
        var isolatedMessages = ChatMessageSourceIsolation.CloneMessages(messages);
        var originalContext = AIAgent.CurrentRunContext;
        EnsureCompatibleCompactionState(originalContext?.Session);
        var compactionContext = CreateCompactionContext(originalContext, options);
        if (compactionContext == null)
        {
            await foreach (
                var update in base.GetStreamingResponseAsync(isolatedMessages, options, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return update;
            }

            yield break;
        }

        RunContextAccessor.SetCurrent(compactionContext);
        try
        {
            await foreach (
                var update in base.GetStreamingResponseAsync(isolatedMessages, options, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return update;
            }
        }
        finally
        {
            // 恢复进入本包装器前的上下文，防止后续调用看到临时会话。
            // Restore the incoming context so later calls cannot observe the temporary session.
            RunContextAccessor.SetCurrent(originalContext);
        }
    }

    /// <summary>
    /// <para>检查会话内的压缩索引版本，移除不兼容旧状态并写入当前版本标记。</para>
    /// <para>Checks the session's compaction-index version, removes incompatible legacy state, and stores the current version marker.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话；可以为空，相关状态处理会按方法规则跳过或委托内层。</para>
    /// <para>Current SDK session; may be null, with state handling skipped or delegated according to the method contract.</para>
    /// </param>
    private void EnsureCompatibleCompactionState(AgentSession? session)
    {
        if (
            session == null
            || string.IsNullOrWhiteSpace(_compactionStateKey)
            || string.IsNullOrWhiteSpace(_compactionVersionStateKey)
        )
        {
            return;
        }

        if (
            session.StateBag.TryGetValue<string>(_compactionVersionStateKey, out var version)
            && string.Equals(version, CompactionIndexVersion, StringComparison.Ordinal)
        )
        {
            return;
        }

        // 旧索引语义不兼容时丢弃压缩状态，由完整历史重建，避免遗漏消息。
        // Discard incompatible compaction state so complete history rebuilds the index without missing messages.
        var resetExistingState = session.StateBag.TryRemoveValue(_compactionStateKey);
        session.StateBag.SetValue(_compactionVersionStateKey, CompactionIndexVersion);
        if (resetExistingState)
        {
            _logger.LogWarning(
                "Resetting legacy compaction state {CompactionStateKey} so it can be rebuilt from complete chat history.",
                _compactionStateKey
            );
        }
    }

    /// <summary>
    /// <para>仅在本地历史会话带 SDK 哨兵 ID 且请求未指定远程会话时创建临时运行上下文。</para>
    /// <para>Creates a temporary run context only for a local-history SDK sentinel session when no remote conversation is specified in the request.</para>
    /// </summary>
    /// <param name="runContext">
    /// <para>SDK 原运行上下文；缺失时不能建立会话压缩作用域。</para>
    /// <para>Original SDK run context; without it a session compaction scope cannot be created.</para>
    /// </param>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <returns>
    /// <para>共享原状态的临时上下文；不需要适配时为空。</para>
    /// <para>Temporary context sharing original state, or null when adaptation is unnecessary.</para>
    /// </returns>
    private static AgentRunContext? CreateCompactionContext(AgentRunContext? runContext, ChatOptions? options)
    {
        if (runContext?.Session == null || !string.IsNullOrWhiteSpace(options?.ConversationId))
        {
            return null;
        }

        var chatClientSession = runContext.Session.GetService<ChatClientAgentSession>();
        if (string.IsNullOrWhiteSpace(chatClientSession?.ConversationId))
        {
            return null;
        }

        // 首次模型调用后 SDK 会设置本地会话哨兵；临时隐藏该服务，防止后续工具循环误跳过压缩。
        // Per-service-call persistence assigns a local conversation sentinel after the first
        // model call. CompactionProvider treats every conversation id as remotely managed and
        // would otherwise skip the remaining calls in the function invocation loop.
        var localHistorySession = new LocalHistorySession(runContext.Session);
        return new AgentRunContext(
            runContext.Agent,
            localHistorySession,
            runContext.RequestMessages,
            runContext.RunOptions
        );
    }

    /// <summary>
    /// <para>共享原会话状态，但隐藏会使压缩跳过的 ChatClientAgentSession 服务。</para>
    /// <para>Shares the original session state while hiding the ChatClientAgentSession service that would bypass compaction.</para>
    /// </summary>
    private sealed class LocalHistorySession : AgentSession
    {
        private readonly AgentSession _innerSession;

        /// <summary>
        /// <para>创建 LocalHistorySession 实例并保存本包装层使用的依赖和配置。</para>
        /// <para>Initializes LocalHistorySession with the dependencies and configuration used by this wrapper.</para>
        /// </summary>
        /// <param name="innerSession">
        /// <para>共享状态并提供其余服务的原始会话。</para>
        /// <para>Original session whose state is shared and whose other services remain available.</para>
        /// </param>
        public LocalHistorySession(AgentSession innerSession)
            : base(innerSession.StateBag)
        {
            _innerSession = innerSession;
        }

        /// <summary>
        /// <para>隐藏无键的 ChatClientAgentSession 查询，其余服务查询委托原会话和基类。</para>
        /// <para>Hides unkeyed ChatClientAgentSession lookups and delegates other service lookups to the original session and base implementation.</para>
        /// </summary>
        /// <param name="serviceType">
        /// <para>请求的服务类型。</para>
        /// <para>Requested service type.</para>
        /// </param>
        /// <param name="serviceKey">
        /// <para>可选服务键；空值表示无键查询。</para>
        /// <para>Optional service key; null denotes an unkeyed lookup.</para>
        /// </param>
        /// <returns>
        /// <para>匹配的服务；被隐藏或无法解析时为空。</para>
        /// <para>Matching service, or null when hidden or unavailable.</para>
        /// </returns>
        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceKey == null && serviceType == typeof(ChatClientAgentSession))
            {
                return null;
            }

            return _innerSession.GetService(serviceType, serviceKey) ?? base.GetService(serviceType, serviceKey);
        }
    }

    /// <summary>
    /// <para>提供对 SDK 当前运行上下文的受保护设置入口。</para>
    /// <para>Exposes the protected setter for the SDK's current run context.</para>
    /// </summary>
    private abstract class RunContextAccessor : AIAgent
    {
        /// <summary>
        /// <para>替换 SDK 当前执行上下文；调用方负责在 finally 中恢复旧值。</para>
        /// <para>Replaces the SDK current run context; callers must restore the previous value in finally.</para>
        /// </summary>
        /// <param name="context">
        /// <para>要设置的 SDK 运行上下文；为空时清空当前上下文。</para>
        /// <para>SDK run context to set; null clears the current context.</para>
        /// </param>
        public static void SetCurrent(AgentRunContext? context)
        {
            CurrentRunContext = context;
        }
    }
}
