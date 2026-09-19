using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.History;

/// <summary>
/// <para>隔离工具循环的消息副本，并过滤同一外层执行中重复注入的上下文。</para>
/// <para>Isolates function-loop message copies and filters repeated context injection within one outer run.</para>
/// </summary>
internal sealed class FunctionLoopMessageIsolationChatClient : DelegatingChatClient
{
    private readonly FunctionLoopContextTracker _contextTracker;

    /// <summary>
    /// <para>创建 FunctionLoopMessageIsolationChatClient 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes FunctionLoopMessageIsolationChatClient with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerClient">
    /// <para>接收处理后请求的内层聊天客户端。</para>
    /// <para>Inner chat client receiving the processed request.</para>
    /// </param>
    /// <param name="contextTracker">
    /// <para>本次 Agent 管道共用的上下文去重跟踪器。</para>
    /// <para>Context-deduplication tracker shared by this agent pipeline.</para>
    /// </param>
    public FunctionLoopMessageIsolationChatClient(IChatClient innerClient, FunctionLoopContextTracker contextTracker)
        : base(innerClient)
    {
        _contextTracker = contextTracker;
    }

    /// <summary>
    /// <para>复制消息及来源属性，过滤本次执行中未变化的上下文后调用模型。</para>
    /// <para>Calls the model with copied messages and attribution after filtering unchanged context in this run.</para>
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
            _contextTracker.FilterRepeatedContextMessages(ChatMessageSourceIsolation.CloneMessages(messages)),
            options,
            cancellationToken
        );

    /// <summary>
    /// <para>复制消息及来源属性，过滤本次执行中未变化的上下文后调用模型。</para>
    /// <para>Calls the model with copied messages and attribution after filtering unchanged context in this run.</para>
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
            _contextTracker.FilterRepeatedContextMessages(ChatMessageSourceIsolation.CloneMessages(messages)),
            options,
            cancellationToken
        );
}

/// <summary>
/// <para>为一次调用建立上下文去重作用域，使自动审批重入共享记录、后续回合重新开始。</para>
/// <para>Establishes a context-deduplication scope shared by approval re-entry within a run and reset for later turns.</para>
/// </summary>
internal sealed class FunctionLoopContextScopeAgent : DelegatingAIAgent
{
    private readonly FunctionLoopContextTracker _contextTracker;

    /// <summary>
    /// <para>创建 FunctionLoopContextScopeAgent 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes FunctionLoopContextScopeAgent with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="contextTracker">
    /// <para>本次 Agent 管道共用的上下文去重跟踪器。</para>
    /// <para>Context-deduplication tracker shared by this agent pipeline.</para>
    /// </param>
    public FunctionLoopContextScopeAgent(AIAgent innerAgent, FunctionLoopContextTracker contextTracker)
        : base(innerAgent)
    {
        _contextTracker = contextTracker;
    }

    /// <summary>
    /// <para>确保存在会话，并在当前执行作用域内转发调用；退出时释放去重记录。</para>
    /// <para>Ensures a session exists and forwards within a run scope, releasing deduplication state on exit.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；为空时向内层 Agent 创建新会话。</para>
    /// <para>Current SDK session; null creates a new session through the inner agent.</para>
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
        session ??= await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _contextTracker.BeginRun(session);
        return await InnerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <para>确保存在会话，并在当前执行作用域内转发调用；退出时释放去重记录。</para>
    /// <para>Ensures a session exists and forwards within a run scope, releasing deduplication state on exit.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话；为空时向内层 Agent 创建新会话。</para>
    /// <para>Current SDK session; null creates a new session through the inner agent.</para>
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
        session ??= await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _contextTracker.BeginRun(session);
        await foreach (
            var update in InnerAgent
                .RunStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return update;
        }
    }
}

/// <summary>
/// <para>按 SDK 会话实例跟踪当前执行已发送的上下文签名。</para>
/// <para>Tracks context signatures already sent in the current run, keyed by SDK session identity.</para>
/// </summary>
/// <remarks>
/// <para>只有纯文本或推理文本上下文参与签名去重。来源序号参与比较；新执行替换旧记录，旧作用域释放不能删除新记录。</para>
/// <para>Only text and reasoning-text context participates in signature deduplication. Source ordinals are part of the key; a new run replaces the old record, and disposing the old scope cannot remove the replacement.</para>
/// </remarks>
internal sealed class FunctionLoopContextTracker
{
    private readonly ConcurrentDictionary<AgentSession, RunState> _runs = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// <para>为指定会话创建新的去重记录，并返回只会清理该记录的作用域。</para>
    /// <para>Creates a fresh deduplication record for the session and returns a scope that removes only that record.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话及其状态。</para>
    /// <para>Current SDK session and its state.</para>
    /// </param>
    /// <returns>
    /// <para>释放时移除本次执行记录的句柄。</para>
    /// <para>Handle that removes this run's record when disposed.</para>
    /// </returns>
    public IDisposable BeginRun(AgentSession session)
    {
        // 按会话替换为本次执行的独立记录；自动审批重入在该记录内去重。
        // Replace the session entry with a run-local record shared by approval re-entry.
        var run = new RunState();
        _runs[session] = run;
        return new RunScope(this, session, run);
    }

    /// <summary>
    /// <para>比较同来源同序号的上下文签名，保留变更或不可签名的消息并补齐消息 ID。</para>
    /// <para>Compares context signatures at matching source positions, retaining changed or unsignable messages and supplying missing message IDs.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <returns>
    /// <para>保留顺序的消息列表；无活动会话记录时返回原列表。</para>
    /// <para>Ordered retained messages, or the original list when no active session record exists.</para>
    /// </returns>
    public List<ChatMessage> FilterRepeatedContextMessages(List<ChatMessage> messages)
    {
        var session = AIAgent.CurrentRunContext?.Session;
        if (session == null || !_runs.TryGetValue(session, out var run))
        {
            return messages;
        }

        var filtered = new List<ChatMessage>(messages.Count);
        var sourceOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider)
            {
                filtered.Add(message);
                continue;
            }

            if (TryCreateSignature(message, out var signature))
            {
                var sourceId = message.GetAgentRequestMessageSourceId() ?? string.Empty;
                sourceOrdinals.TryGetValue(sourceId, out var ordinal);
                sourceOrdinals[sourceId] = ordinal + 1;
                var key = new ContextMessageKey(sourceId, ordinal);
                if (run.ContextMessages.TryGetValue(key, out var previous) && previous == signature)
                {
                    continue;
                }

                run.ContextMessages[key] = signature;
            }

            // 压缩在缺少 MessageId 时按内容匹配；为当前副本补 ID，避免下一回合误用旧游标。
            // Compaction falls back to content equality when MessageId is null. Give each
            // emitted context copy an identity so a later turn cannot match an older cursor.
            message.MessageId ??= Guid.CreateVersion7().ToString("N");
            filtered.Add(message);
        }

        return filtered;
    }

    /// <summary>
    /// <para>为仅含文本和推理文本的消息生成包含角色和作者的签名。</para>
    /// <para>Builds a role-and-author-aware signature for messages containing only text and reasoning text.</para>
    /// </summary>
    /// <param name="message">
    /// <para>待检查、复制或补充元数据的消息。</para>
    /// <para>Message to inspect, copy, or annotate.</para>
    /// </param>
    /// <param name="signature">
    /// <para>成功时返回生成的上下文签名；失败时为默认值。</para>
    /// <para>Receives the generated context signature on success, or the default value on failure.</para>
    /// </param>
    /// <returns>
    /// <para>仅当全部内容可稳定签名时为 true。</para>
    /// <para>True only when every content item supports a stable signature.</para>
    /// </returns>
    private static bool TryCreateSignature(ChatMessage message, out ContextMessageSignature signature)
    {
        var content = new StringBuilder();
        foreach (var item in message.Contents)
        {
            switch (item)
            {
                case TextContent text:
                    AppendText(content, nameof(TextContent), text.Text);
                    break;
                case TextReasoningContent reasoning:
                    AppendText(content, nameof(TextReasoningContent), reasoning.Text);
                    break;
                default:
                    signature = default;
                    return false;
            }
        }

        signature = new ContextMessageSignature(message.Role.Value, message.AuthorName, content.ToString());
        return true;
    }

    /// <summary>
    /// <para>向签名缓冲区追加内容类型、文本长度和正文，避免拼接歧义。</para>
    /// <para>Appends content kind, text length, and text to the signature buffer to avoid concatenation ambiguity.</para>
    /// </summary>
    /// <param name="builder">
    /// <para>用于累计无歧义内容签名的文本缓冲区。</para>
    /// <para>Text buffer used to accumulate an unambiguous content signature.</para>
    /// </param>
    /// <param name="kind">
    /// <para>内容类型标记，用于区分普通文本与推理文本。</para>
    /// <para>Content-kind marker distinguishing ordinary text from reasoning text.</para>
    /// </param>
    /// <param name="text">
    /// <para>加入签名的原始文本。</para>
    /// <para>Original text included in the signature.</para>
    /// </param>
    private static void AppendText(StringBuilder builder, string kind, string text)
    {
        builder.Append(kind);
        builder.Append(':');
        builder.Append(text.Length);
        builder.Append(':');
        builder.Append(text);
        builder.Append(';');
    }

    /// <summary>
    /// <para>保存单次执行中按来源及位置索引的上下文签名。</para>
    /// <para>Stores context signatures indexed by source and position for one run.</para>
    /// </summary>
    private sealed class RunState
    {
        /// <summary>
        /// <para>当前执行已发送的各来源位置及其最近签名。</para>
        /// <para>Latest signatures sent for each source position in the current run.</para>
        /// </summary>
        public Dictionary<ContextMessageKey, ContextMessageSignature> ContextMessages { get; } = [];
    }

    /// <summary>
    /// <para>持有特定会话和执行记录的释放句柄。</para>
    /// <para>Owns the disposal handle for a particular session and run record.</para>
    /// </summary>
    private sealed class RunScope : IDisposable
    {
        private readonly FunctionLoopContextTracker _owner;
        private readonly AgentSession _session;
        private readonly RunState _run;
        private int _disposed;

        /// <summary>
        /// <para>创建 RunScope 实例并保存本包装层使用的依赖和配置。</para>
        /// <para>Initializes RunScope with the dependencies and configuration used by this wrapper.</para>
        /// </summary>
        /// <param name="owner">
        /// <para>当前作用域或绑定所属的实例。</para>
        /// <para>Instance that owns this scope or binding.</para>
        /// </param>
        /// <param name="session">
        /// <para>当前 SDK 会话及其状态。</para>
        /// <para>Current SDK session and its state.</para>
        /// </param>
        /// <param name="run">
        /// <para>当前作用域持有的执行记录，释放时按实例匹配移除。</para>
        /// <para>Run record owned by the scope, removed by instance match on disposal.</para>
        /// </param>
        public RunScope(FunctionLoopContextTracker owner, AgentSession session, RunState run)
        {
            _owner = owner;
            _session = session;
            _run = run;
        }

        /// <summary>
        /// <para>幂等地移除当前作用域持有的记录，保留之后为同一会话建立的新执行记录。</para>
        /// <para>Idempotently removes the record owned by this scope without deleting a newer run for the same session.</para>
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // 同时匹配会话键与记录实例，旧作用域不能误删新执行的数据。
                // Match both session key and record identity so an old scope cannot remove a newer run.
                _owner._runs.TryRemove(new KeyValuePair<AgentSession, RunState>(_session, _run));
            }
        }
    }

    /// <summary>
    /// <para>使用来源标识和同来源内的序号定位上下文消息。</para>
    /// <para>Identifies a context message by its source identifier and ordinal within that source.</para>
    /// </summary>
    /// <param name="SourceId">
    /// <para>上下文 Provider 的来源标识。</para>
    /// <para>Source identifier of the context provider.</para>
    /// </param>
    /// <param name="Ordinal">
    /// <para>同一来源在当前请求中的零基序号。</para>
    /// <para>Zero-based position of the source within the current request.</para>
    /// </param>
    private readonly record struct ContextMessageKey(string SourceId, int Ordinal);

    /// <summary>
    /// <para>记录上下文的角色、作者和文本内容，用于比较是否需要重新发送。</para>
    /// <para>Captures context role, author, and textual content to decide whether it must be sent again.</para>
    /// </summary>
    /// <param name="Role">
    /// <para>消息角色的字符串值。</para>
    /// <para>String value of the message role.</para>
    /// </param>
    /// <param name="AuthorName">
    /// <para>消息作者名称；可以为空。</para>
    /// <para>Message author name; may be null.</para>
    /// </param>
    /// <param name="Content">
    /// <para>包含类型和长度分隔信息的内容签名。</para>
    /// <para>Content signature including type and length delimiters.</para>
    /// </param>
    private readonly record struct ContextMessageSignature(string Role, string? AuthorName, string Content);
}

/// <summary>
/// <para>复制消息及其来源元数据，隔离历史压缩对来源标记的修改。</para>
/// <para>Copies messages and source metadata to isolate attribution changes made by history compaction.</para>
/// </summary>
internal static class ChatMessageSourceIsolation
{
    // Clone 是浅复制；另拷贝属性字典，防止压缩维护索引时污染原消息的来源标记。
    // ChatMessage.Clone is shallow. Copy the dictionary as well because CompactionProvider
    // replaces source attribution entries while maintaining its incremental message index.
    /// <summary>
    /// <para>浅复制消息并单独复制属性字典，隔离来源标记修改；内容对象仍由 SDK 共享。</para>
    /// <para>Shallow-copies messages and separately copies property dictionaries to isolate attribution changes; content objects remain shared.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <returns>
    /// <para>拥有独立属性字典的消息副本列表。</para>
    /// <para>Message copies with independent property dictionaries.</para>
    /// </returns>
    public static List<ChatMessage> CloneMessages(IEnumerable<ChatMessage> messages) =>
        messages
            .Select(static message =>
            {
                var clone = message.Clone();
                if (message.AdditionalProperties != null)
                {
                    clone.AdditionalProperties = new AdditionalPropertiesDictionary(message.AdditionalProperties);
                }

                return clone;
            })
            .ToList();
}
