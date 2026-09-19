using System.Runtime.CompilerServices;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Context;

/// <summary>
/// <para>暂存原始请求，并将可选记忆注入临时副本后交给内层 Agent。</para>
/// <para>Stages original requests and forwards transient copies enriched with optional memory to the inner agent.</para>
/// </summary>
/// <remarks>
/// <para>历史只暂存原始可持久化输入；记忆增强副本不会重复写入历史。流式释放先完成 SDK 回调，再提交残留请求；清理失败不得覆盖已有执行异常。</para>
/// <para>History stages only original persistable input; memory-enriched copies are excluded from persistence. Streaming disposal finishes SDK callbacks before flushing pending requests, without masking an existing execution failure.</para>
/// </remarks>
internal sealed class AgentRequestContextAgent : DelegatingAIAgent
{
    private const string CurrentRequestHeading = "\n\n## Current Request\n\n";

    private readonly IConversationHistoryRequests _historyProvider;
    private readonly Func<CancellationToken, ValueTask<ChatMessage?>>? _createMemoryContextAsync;
    private readonly ILogger _logger;

    /// <summary>
    /// <para>创建 AgentRequestContextAgent 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes AgentRequestContextAgent with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="historyProvider">
    /// <para>必须提供原始请求暂存能力的历史 Provider。</para>
    /// <para>History provider that must expose original-request staging support.</para>
    /// </param>
    /// <param name="createMemoryContextAsync">
    /// <para>按需生成临时记忆上下文的回调；为空时不注入记忆。</para>
    /// <para>Callback creating transient memory context on demand; null disables memory injection.</para>
    /// </param>
    /// <param name="logger">
    /// <para>记录执行、兼容处理或清理错误的日志器。</para>
    /// <para>Logger for execution, compatibility handling, or cleanup errors.</para>
    /// </param>
    /// <exception cref="AgwException">
    /// <para>历史 Provider 不支持原始请求暂存协议。</para>
    /// <para>The history provider does not support the original-request staging protocol.</para>
    /// </exception>
    public AgentRequestContextAgent(
        AIAgent innerAgent,
        ChatHistoryProvider historyProvider,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync,
        ILogger logger
    )
        : base(innerAgent)
    {
        ArgumentNullException.ThrowIfNull(historyProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _historyProvider =
            historyProvider.GetService<IConversationHistoryRequests>()
            ?? throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "The history provider must support original request persistence."
            );
        _createMemoryContextAsync = createMemoryContextAsync;
        _logger = logger;
    }

    /// <summary>
    /// <para>暂存原始输入，转发临时记忆增强副本，并在退出时提交残留请求。</para>
    /// <para>Stages original input, forwards transient memory-enriched copies, and flushes pending requests on exit.</para>
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
        var requestMessages = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        // 暂存原始输入而非记忆增强副本，避免历史重复或包含临时上下文。
        // Stage original input rather than memory-enriched copies to avoid duplicate history and transient context.
        _historyProvider.StageRequest(safeSession, SelectPersistableRequestMessages(requestMessages));
        Exception? executionFailure = null;
        try
        {
            var forwardedMessages = await CreateForwardedMessagesAsync(requestMessages, cancellationToken)
                .ConfigureAwait(false);
            return await InnerAgent
                .RunAsync(forwardedMessages, safeSession, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            throw;
        }
        finally
        {
            await PersistPendingWithoutMaskingExecutionFailureAsync(safeSession, executionFailure)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>暂存原始输入，转发临时记忆增强副本，并在退出时提交残留请求。</para>
    /// <para>Stages original input, forwards transient memory-enriched copies, and flushes pending requests on exit.</para>
    /// </summary>
    /// <remarks>
    /// <para>分别处理上下文准备、枚举器创建和迭代失败；先释放 SDK 枚举器以完成历史回调，再提交残留输入。</para>
    /// <para>Handles context preparation, enumerator creation, and iteration failures separately; disposes the SDK enumerator to finish history callbacks before flushing pending input.</para>
    /// </remarks>
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
        var requestMessages = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        // 暂存原始输入而非记忆增强副本，避免历史重复或包含临时上下文。
        // Stage original input rather than memory-enriched copies to avoid duplicate history and transient context.
        _historyProvider.StageRequest(safeSession, SelectPersistableRequestMessages(requestMessages));
        Exception? executionFailure = null;
        IReadOnlyList<ChatMessage> forwardedMessages;
        try
        {
            forwardedMessages = await CreateForwardedMessagesAsync(requestMessages, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            await PersistPendingWithoutMaskingExecutionFailureAsync(safeSession, executionFailure)
                .ConfigureAwait(false);
            throw;
        }

        IAsyncEnumerator<AgentResponseUpdate> enumerator;
        try
        {
            enumerator = InnerAgent
                .RunStreamingAsync(forwardedMessages, safeSession, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            await PersistPendingWithoutMaskingExecutionFailureAsync(safeSession, executionFailure)
                .ConfigureAwait(false);
            throw;
        }

        try
        {
            while (true)
            {
                AgentResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;
                    update = enumerator.Current;
                }
                catch (Exception exception)
                {
                    executionFailure = exception;
                    throw;
                }
                yield return update;
            }
        }
        finally
        {
            // SDK 释放时仍可能触发历史回调；等待回调结束后再清理暂存请求和流状态。
            // SDK disposal may still notify history. Keep request/stream state until those callbacks finish.
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (executionFailure != null)
            {
                _logger.LogError(exception, "Agent stream disposal failed while preserving an execution failure.");
            }
            catch (Exception exception)
            {
                executionFailure = exception;
                throw;
            }
            finally
            {
                await PersistPendingWithoutMaskingExecutionFailureAsync(safeSession, executionFailure)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// <para>复制请求并排除临时副本的持久化；存在记忆时，将其加入第一条外部用户请求的前缀。</para>
    /// <para>Copies requests with persistence excluded and, when memory exists, prefixes the first external user request with it.</para>
    /// </summary>
    /// <param name="requestMessages">
    /// <para>当前操作使用的请求消息集合。</para>
    /// <para>Request messages used by the current operation.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>仅用于当前模型调用的请求副本，按需包含记忆前缀。</para>
    /// <para>Request copies for the current model call only, with an optional memory prefix.</para>
    /// </returns>
    private async Task<IReadOnlyList<ChatMessage>> CreateForwardedMessagesAsync(
        IReadOnlyList<ChatMessage> requestMessages,
        CancellationToken cancellationToken
    )
    {
        var forwardedMessages = requestMessages.Select(CloneForTransientForwarding).ToList();
        if (_createMemoryContextAsync == null)
        {
            return forwardedMessages;
        }

        var memoryMessage = await _createMemoryContextAsync(cancellationToken).ConfigureAwait(false);
        if (memoryMessage == null || string.IsNullOrWhiteSpace(memoryMessage.Text))
        {
            return forwardedMessages;
        }

        // 记忆只增强第一条真正的外部用户请求，不改写工具结果或内部上下文消息。
        // Enrich only the first actual external user request, not tool results or internal context messages.
        var requestIndex = forwardedMessages.FindIndex(message =>
            message.Role == ChatRole.User
            && message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External
        );
        if (requestIndex < 0)
        {
            return forwardedMessages;
        }

        var composite = forwardedMessages[requestIndex];
        composite.Contents = [new TextContent(memoryMessage.Text + CurrentRequestHeading), .. composite.Contents];
        composite = composite.WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            memoryMessage.GetAgentRequestMessageSourceId() ?? ConversationHistoryMetadata.UserMemorySourceId
        );
        ConversationHistoryMetadata.ExcludeFromPersistence(composite);
        forwardedMessages[requestIndex] = composite;
        return forwardedMessages;
    }

    /// <summary>
    /// <para>复制消息、内容列表和属性字典，并将副本标记为不可持久化。</para>
    /// <para>Copies the message, content list, and property dictionary, marking the copy as excluded from persistence.</para>
    /// </summary>
    /// <param name="message">
    /// <para>待检查、复制或补充元数据的消息。</para>
    /// <para>Message to inspect, copy, or annotate.</para>
    /// </param>
    /// <returns>
    /// <para>已排除持久化的临时消息副本。</para>
    /// <para>Transient message copy excluded from persistence.</para>
    /// </returns>
    private static ChatMessage CloneForTransientForwarding(ChatMessage message)
    {
        var clone = message.Clone();
        clone.Contents = message.Contents.ToList();
        if (message.AdditionalProperties != null)
        {
            clone.AdditionalProperties = new AdditionalPropertiesDictionary(message.AdditionalProperties);
        }
        ConversationHistoryMetadata.ExcludeFromPersistence(clone);
        return clone;
    }

    /// <summary>
    /// <para>筛选允许持久化的原始请求，并转换框架审批包装以便序列化审计。</para>
    /// <para>Selects persistable original requests and converts framework approval wrappers for serializable auditing.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <returns>
    /// <para>可安全暂存和序列化的原始请求消息。</para>
    /// <para>Original request messages safe for staging and serialization.</para>
    /// </returns>
    private static IReadOnlyList<ChatMessage> SelectPersistableRequestMessages(IEnumerable<ChatMessage> messages) =>
        messages
            .Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message))
            .Select(CreatePersistableRequestMessage)
            .ToList();

    /// <summary>
    /// <para>将永久授权包装还原为标准审批响应，并把审批控制消息排除出模型历史。</para>
    /// <para>Unwraps standing-approval content into standard approval responses and excludes approval control messages from model history.</para>
    /// </summary>
    /// <param name="message">
    /// <para>待检查、复制或补充元数据的消息。</para>
    /// <para>Message to inspect, copy, or annotate.</para>
    /// </param>
    /// <returns>
    /// <para>可审计的审批消息副本，或无需转换的原消息。</para>
    /// <para>Auditable approval-message copy, or the original message when no conversion is needed.</para>
    /// </returns>
    private static ChatMessage CreatePersistableRequestMessage(ChatMessage message)
    {
        if (
            !message.Contents.Any(static content =>
                content
                    is AlwaysApproveToolApprovalResponseContent
                        or ToolApprovalResponseContent { ToolCall: FunctionCallContent }
            )
        )
        {
            return message;
        }

        // 审批控制消息可用于审计，但必须排除出模型历史和跨 Agent 交接；先拆开不可序列化的 MAF 包装。
        // Persist standard approval responses for audit, but keep these framework control messages
        // out of model history and cross-Agent handoff. The MAF wrapper itself is not JSON-serializable.
        var persistableMessage = message.Clone();
        if (message.AdditionalProperties != null)
        {
            persistableMessage.AdditionalProperties = new AdditionalPropertiesDictionary(message.AdditionalProperties);
        }

        persistableMessage.Contents = message
            .Contents.Select(static content =>
                content is AlwaysApproveToolApprovalResponseContent approval ? approval.InnerResponse : content
            )
            .ToList();
        ConversationHistoryMetadata.ExcludeFromModelHistory(persistableMessage);
        return persistableMessage;
    }

    /// <summary>
    /// <para>使用不可取消令牌提交残留请求；已有执行异常时只记录提交错误，否则传播提交错误。</para>
    /// <para>Flushes pending requests with a non-cancellable token, logging flush errors when execution already failed and otherwise propagating them.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话及其状态。</para>
    /// <para>Current SDK session and its state.</para>
    /// </param>
    /// <param name="executionFailure">
    /// <para>已捕获的执行异常；非空时清理失败不能覆盖它。</para>
    /// <para>Previously captured execution failure; cleanup errors must not mask it when present.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private async Task PersistPendingWithoutMaskingExecutionFailureAsync(
        AgentSession session,
        Exception? executionFailure
    )
    {
        try
        {
            await _historyProvider.PersistPendingAsync(this, session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (executionFailure != null)
        {
            _logger.LogError(exception, "Agent request persistence failed while preserving an execution failure.");
        }
    }
}
