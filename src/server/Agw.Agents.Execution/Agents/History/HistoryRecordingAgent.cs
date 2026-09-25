using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Context;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// 包在每个 Agent 外层的历史记录：暂存原始输入，每个 Turn 注入一次记忆上下文，用 Engine 适配器把流式增量归并进历史并盖上 Step 序号。
/// The history recorder around every Agent: stages the original input, injects memory context once per turn, and merges streaming deltas into history through the Engine adapter with their Step index.
/// </summary>
/// <remarks>
/// 记忆增强的请求只是临时副本，不进入历史。External Engine 的 Step 以工具结果之后的新响应为界；System Agent 的 Step 由 AgwChatHistoryProvider 按模型调用推进。
/// Memory-enriched requests are transient copies kept out of history. External Engine Steps are bounded by a new response after tool results; System Agent Steps advance per model call in AgwChatHistoryProvider.
/// </remarks>
internal sealed class HistoryRecordingAgent : DelegatingAIAgent
{
    private const string CurrentRequestHeading = "\n\n## Current Request\n\n";

    private readonly AgwChatHistoryProvider _history;
    private readonly Func<CancellationToken, ValueTask<ChatMessage?>>? _createMemoryContextAsync;
    private readonly ILogger _logger;

    public HistoryRecordingAgent(
        AIAgent innerAgent,
        AgwChatHistoryProvider history,
        Func<CancellationToken, ValueTask<ChatMessage?>>? createMemoryContextAsync,
        ILogger logger
    )
        : base(innerAgent)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(logger);
        _history = history;
        _createMemoryContextAsync = createMemoryContextAsync;
        _logger = logger;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var input = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var recording = _history.Begin(this, safeSession);
        recording.Stage(SelectPersistableInput(input));
        var completed = false;
        Exception? failure = null;
        try
        {
            var forwarded = await CreateForwardedMessagesAsync(input, cancellationToken).ConfigureAwait(false);
            var response = await InnerAgent
                .RunAsync(forwarded, safeSession, options, cancellationToken)
                .ConfigureAwait(false);
            await PersistStagedInputAsync(recording, cancellationToken).ConfigureAwait(false);
            // System Agent 的每次模型调用已由 Provider 校准；External Engine 的完整响应在这里写入。
            // Each model call of a System Agent was already calibrated by the provider; External Engine responses are written here.
            if (!_history.TracksSteps)
                await recording
                    .CalibrateAsync(response.Messages.ToList(), null, cancellationToken)
                    .ConfigureAwait(false);
            completed = true;
            return response;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await CloseAsync(recording, safeSession, completed, failure).ConfigureAwait(false);
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var input = messages.ToList();
        var safeSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var recording = _history.Begin(this, safeSession);
        recording.Stage(SelectPersistableInput(input));
        var completed = false;
        Exception? failure = null;
        using var innerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
        var disposed = false;
        var afterToolResult = false;
        try
        {
            IReadOnlyList<ChatMessage> forwarded;
            try
            {
                forwarded = await CreateForwardedMessagesAsync(input, cancellationToken).ConfigureAwait(false);
                enumerator = InnerAgent
                    .RunStreamingAsync(forwarded, safeSession, options, innerCancellation.Token)
                    .GetAsyncEnumerator(innerCancellation.Token);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }

            while (true)
            {
                AgentResponseUpdate update;
                bool handled;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;
                    update = enumerator.Current;
                    // External Engine 在第一段输出之前写入原始输入。System Agent 的输入由 Provider 在读取历史之后写入：
                    // 批准的工具结果会在模型调用之前流出，提前写入会让输入同时出现在历史与请求里。
                    // External Engines write the original input before their first output. A System Agent's input is written by the provider after it reads history:
                    // approved tool results stream out before the model call, and writing earlier would put the input in both history and the request.
                    if (!_history.TracksSteps)
                    {
                        await PersistStagedInputAsync(recording, cancellationToken).ConfigureAwait(false);
                        afterToolResult = AdvanceExternalStep(update, afterToolResult);
                    }
                    else if (ToToolResultMessage(update) is { } toolResult)
                    {
                        // 工具结果在输出流中先于下一次模型请求出现；流式副本与请求副本写入同一行。
                        // Tool results appear in the stream before the next model request; the streamed and requested copies write one row.
                        await recording
                            .PutToolResultAsync(toolResult, ExecutionScope.Current?.StepIndex, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    // 延续运行开始时 FICC 重新输出已批准或已拒绝的调用，它们已随上一 Step 的响应写入。
                    // At the start of a continuation run FICC re-emits the approved or rejected calls, which were written with the previous Step's response.
                    var replayedCalls =
                        _history.TracksSteps
                        && !recording.ModelCallStarted
                        && update.Contents.Any(content => content is FunctionCallContent);
                    handled =
                        !replayedCalls && await recording.RecordAsync(update, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }

                if (!handled)
                {
                    yield return update;
                    continue;
                }
                foreach (var normalized in recording.Drain())
                    yield return normalized;
                // 未写入历史的内容（使用量、审批请求、System 的工具结果）继续交给调用方。
                // Content kept out of history (usage, approval requests, System tool results) still reaches the caller.
                if (Remaining(update, recording.Adapter) is { } remaining)
                    yield return remaining;
            }

            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                disposed = true;
            }
            await PersistStagedInputAsync(recording, cancellationToken).ConfigureAwait(false);
            completed = true;
            foreach (var normalized in recording.Drain())
                yield return normalized;
        }
        finally
        {
            try
            {
                // 先释放 SDK，再结束记录，SDK 收尾时的历史回调仍写入本次投影。
                // Dispose the SDK before ending the recording so final history callbacks still target this projection.
                if (enumerator != null && !disposed)
                {
                    innerCancellation.Cancel();
                    try
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (failure != null)
                    {
                        _logger.LogError(exception, "SDK cleanup failed after an execution failure.");
                    }
                }
            }
            finally
            {
                await CloseAsync(recording, safeSession, completed, failure).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// External Engine 在工具结果之后开始新的响应时进入下一个 Step；第一段输出开始第一个 Step。
    /// An External Engine enters the next Step when a new response starts after tool results; the first output starts the first Step.
    /// </summary>
    private static bool AdvanceExternalStep(AgentResponseUpdate update, bool afterToolResult)
    {
        if (ExecutionScope.Current is not { } scope)
            return afterToolResult;
        if (update.Contents.Any(content => content is FunctionResultContent))
            return true;
        var responds =
            (update.Role ?? ChatRole.Assistant) == ChatRole.Assistant
            && update.Contents.Any(content => content is TextContent or TextReasoningContent or FunctionCallContent);
        if (responds && (scope.StepIndex == 0 || afterToolResult))
        {
            scope.AdvanceStep();
            return false;
        }
        return afterToolResult;
    }

    private async Task CloseAsync(HistoryRecording recording, AgentSession session, bool completed, Exception? failure)
    {
        try
        {
            await PersistStagedInputAsync(recording, CancellationToken.None).ConfigureAwait(false);
            await recording.FinishAsync(completed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (failure != null)
        {
            _logger.LogError(exception, "History recording failed to close after an execution failure.");
        }
        finally
        {
            _history.End(session);
        }
    }

    private static async ValueTask PersistStagedInputAsync(
        HistoryRecording recording,
        CancellationToken cancellationToken
    )
    {
        if (recording.TakeStagedInput() is { } input)
            await recording.WriteMessagesAsync(input, null, cancellationToken).ConfigureAwait(false);
    }

    private static ChatMessage? ToToolResultMessage(AgentResponseUpdate update)
    {
        var results = update.Contents.OfType<FunctionResultContent>().Cast<AIContent>().ToList();
        return results.Count == 0
            ? null
            : new ChatMessage(update.Role ?? ChatRole.Tool, results)
            {
                MessageId = update.MessageId,
                AuthorName = update.AuthorName,
                CreatedAt = update.CreatedAt,
                AdditionalProperties = update.AdditionalProperties == null ? null : new(update.AdditionalProperties),
            };
    }

    private static AgentResponseUpdate? Remaining(
        AgentResponseUpdate update,
        IAgentMessageAdapter<AgentResponseUpdate> adapter
    )
    {
        var contents = update.Contents.Where(content => !adapter.Records(content)).ToList();
        if (contents.Count == 0)
            return null;
        return new AgentResponseUpdate
        {
            Role = contents.All(content => content is UsageContent) ? ChatRole.System : update.Role,
            AuthorName = update.AuthorName,
            Contents = contents,
            AdditionalProperties = update.AdditionalProperties,
            MessageId = contents.All(content => content is UsageContent) ? null : update.MessageId,
            CreatedAt = update.CreatedAt,
        };
    }

    /// <summary>
    /// 复制请求并排除临时副本的持久化；本 Turn 第一次运行时把记忆加入第一条外部用户请求的前缀。
    /// Copies requests with persistence excluded; on this turn's first run the memory prefixes the first external user request.
    /// </summary>
    private async Task<IReadOnlyList<ChatMessage>> CreateForwardedMessagesAsync(
        IReadOnlyList<ChatMessage> requestMessages,
        CancellationToken cancellationToken
    )
    {
        var forwardedMessages = requestMessages.Select(CloneForTransientForwarding).ToList();
        if (_createMemoryContextAsync == null || ExecutionScope.Current?.TryBeginMemoryInjection() == false)
            return forwardedMessages;

        var requestIndex = forwardedMessages.FindIndex(message =>
            message.Role == ChatRole.User
            && message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External
            && !message.Contents.Any(content => content is ToolApprovalResponseContent)
        );
        if (requestIndex < 0)
            return forwardedMessages;

        var memoryMessage = await _createMemoryContextAsync(cancellationToken).ConfigureAwait(false);
        if (memoryMessage == null || string.IsNullOrWhiteSpace(memoryMessage.Text))
            return forwardedMessages;

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

    private static ChatMessage CloneForTransientForwarding(ChatMessage message)
    {
        var clone = message.Clone();
        clone.Contents = message.Contents.ToList();
        if (message.AdditionalProperties != null)
            clone.AdditionalProperties = new AdditionalPropertiesDictionary(message.AdditionalProperties);
        ConversationHistoryMetadata.ExcludeFromPersistence(clone);
        return clone;
    }

    /// <summary>
    /// 选出可持久化的原始输入；审批响应保留用于审计，但不进入模型历史与跨 Agent 交接。MAF 的永久授权包装不能序列化，写入其中的标准审批响应。
    /// Selects the persistable original input; approval responses are kept for audit but stay out of model history and cross-Agent handoff. The MAF standing-approval wrapper is not serializable, so its standard approval response is written.
    /// </summary>
    private static IReadOnlyList<ChatMessage> SelectPersistableInput(IEnumerable<ChatMessage> messages) =>
        messages
            .Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message))
            .Select(message =>
            {
                if (
                    !message.Contents.Any(content =>
                        content
                            is AlwaysApproveToolApprovalResponseContent
                                or ToolApprovalResponseContent { ToolCall: FunctionCallContent }
                    )
                )
                    return message;
                var audit = message.Clone();
                if (message.AdditionalProperties != null)
                    audit.AdditionalProperties = new AdditionalPropertiesDictionary(message.AdditionalProperties);
                audit.Contents = message
                    .Contents.Select(content =>
                        content is AlwaysApproveToolApprovalResponseContent approval ? approval.InnerResponse : content
                    )
                    .ToList();
                ConversationHistoryMetadata.ExcludeFromModelHistory(audit);
                return audit;
            })
            .ToList();
}
