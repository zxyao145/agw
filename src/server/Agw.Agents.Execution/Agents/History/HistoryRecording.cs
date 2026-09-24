using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Agents.Execution.Context;
using Agw.Projects.Contracts.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// 一次 Agent 运行的历史记录：适配器归并的增量与完整消息写入同一个投影，再交给本 Turn 的历史缓冲。
/// The history recording of one Agent run: adapter-merged deltas and complete messages go into one projection, which is handed to this turn's history buffer.
/// </summary>
internal sealed class HistoryRecording
{
    private readonly ConversationMessageWriteScope _scope;
    private readonly IConversationHistoryStore _store;
    private readonly IAgentMessageAdapter<AgentResponseUpdate> _adapter;
    private readonly AgentMessageProjection _projection;
    private readonly ConcurrentQueue<ChatMessage> _outgoing = new();
    private readonly SemaphoreSlim _directWrites = new(1, 1);
    private readonly string? _agentName;
    private readonly bool _structuredResult;
    private List<ChatMessage>? _stagedInput;
    private bool _failed;

    /// <summary>
    /// structuredResult 为真时 Result 使用 json 格式（配置了 ResponseSchema），否则缺省为 markdown。
    /// When structuredResult is true the Result uses the json format (a ResponseSchema is configured); otherwise it defaults to markdown.
    /// </summary>
    internal HistoryRecording(
        ConversationMessageWriteScope scope,
        IConversationHistoryStore store,
        IAgentMessageAdapter<AgentResponseUpdate> adapter,
        TimeProvider timeProvider,
        string? agentName,
        bool transient,
        bool structuredResult
    )
    {
        _scope = scope;
        _store = store;
        _adapter = adapter;
        _projection = new AgentMessageProjection(scope, timeProvider);
        _agentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim();
        Transient = transient;
        _structuredResult = structuredResult;
    }

    /// <summary>
    /// 为真时记录只覆盖一次模型调用，由 Provider 在调用结束时收尾。
    /// When true the recording covers one model call and the provider finishes it when the call ends.
    /// </summary>
    internal bool Transient { get; }

    internal IAgentMessageAdapter<AgentResponseUpdate> Adapter => _adapter;

    /// <summary>
    /// 本次运行是否已经开始过模型调用；由 Provider 在模型调用前标记。
    /// Whether this run has started a model call; marked by the provider before the call.
    /// </summary>
    internal bool ModelCallStarted { get; private set; }

    internal void MarkModelCall() => ModelCallStarted = true;

    /// <summary>
    /// 暂存本次运行的原始输入，在第一次模型请求前写入历史。
    /// Stages this run's original input, written to history before the first model request.
    /// </summary>
    internal void Stage(IReadOnlyList<ChatMessage> input) => _stagedInput = input.ToList();

    internal IReadOnlyList<ChatMessage>? TakeStagedInput() => Interlocked.Exchange(ref _stagedInput, null);

    /// <summary>
    /// 按顺序写入完整消息：临时副本与交接前缀不写入，只去掉空白文本（空推理可能带签名）。
    /// 受理时已经写入的用户输入不再写入对话作用域；节点作用域写入它的副本时使用由作用域与消息 ID 生成的行 Id。
    /// Writes complete messages in order: transient copies and handoff prefixes are skipped, and only blank text is removed (empty reasoning may carry a signature).
    /// The user input written at acceptance is not written to the conversation scope again; a node scope writes its copy under a row Id generated from the scope and the message ID.
    /// </summary>
    internal async ValueTask WriteMessagesAsync(
        IEnumerable<ChatMessage> messages,
        int? stepIndex,
        CancellationToken cancellationToken
    )
    {
        var acceptedInput = ExecutionScope.Current?.Turn?.InputMessageId;
        foreach (var message in messages)
        {
            if (ConversationHistoryMetadata.IsPersistenceExcluded(message))
                continue;
            if (ConversationHandoffMetadata.IsHandoffMessage(message))
                continue;
            var isAcceptedInput =
                acceptedInput.HasValue
                && Guid.TryParse(message.MessageId, out var messageId)
                && messageId == acceptedInput.Value;
            if (isAcceptedInput && _scope.HistoryScope == null)
                continue;
            var contents = message
                .Contents.Where(content =>
                    content is not TextContent text
                    || message.AdditionalProperties.IsToolMessage()
                    || !string.IsNullOrWhiteSpace(text.Text)
                )
                .ToList();
            if (contents.Count == 0)
                continue;
            var persisted = message.Clone();
            persisted.Contents = contents;
            persisted.RawRepresentation = null;
            if (isAcceptedInput)
                persisted.MessageId = CreateDeterministicGuid($"{_scope.HistoryScope}:{acceptedInput!.Value:N}")
                    .ToString("D");
            await PutAsync(persisted, stepIndex, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 写入一段流式增量，返回是否记录了其中的内容；记录后的规范消息通过 Drain 交给调用方。
    /// Writes one streaming delta and returns whether content was recorded; the normalized messages reach the caller through Drain.
    /// </summary>
    internal async ValueTask<bool> RecordAsync(AgentResponseUpdate update, CancellationToken cancellationToken)
    {
        _failed |= update.Contents.OfType<ErrorContent>().Any(ModelMessageAdapter.IsFatalError);
        if (_adapter.Map(update) is not { } message)
            return false;
        PrepareHeader(message);
        var appended = _projection.Append(message, stepIndex: ExecutionScope.Current?.StepIndex);
        _outgoing.Enqueue(appended);
        await ScheduleAsync(EstimateBytes(appended), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 写入一条输入；MessageId 为 GUID 时就是行 Id。输入不带 Agent 的展示信息。
    /// Writes one input; a GUID MessageId is the row Id. Inputs carry no Agent display metadata.
    /// </summary>
    internal ValueTask PutAsync(ChatMessage message, int? stepIndex, CancellationToken cancellationToken) =>
        WriteAsync(message.Clone(), stepIndex, replaceStreamed: true, cancellationToken);

    /// <summary>
    /// 写入一条工具结果消息：行 Id 由 turnId 与其中的 callId 生成。流式输出与下一次模型请求写入同一行，
    /// 从存档恢复后重跑也更新同一行；SDK 合成的消息 ID 不参与行身份。
    /// Writes one tool result message whose row Id is generated from the turnId and its callIds. The streamed copy and the next model request write the same row,
    /// and a rerun after recovery updates it again; message IDs synthesized by the SDK take no part in the row identity.
    /// </summary>
    internal ValueTask PutToolResultAsync(ChatMessage message, int? stepIndex, CancellationToken cancellationToken)
    {
        var stable = message.Clone();
        stable.Contents = message.Contents.ToList();
        var callIds = message
            .Contents.OfType<FunctionResultContent>()
            .Select(content => content.CallId)
            .Order(StringComparer.Ordinal);
        stable.MessageId =
            ExecutionScope.Current?.Context.TurnId is { } turnId
                ? CreateDeterministicGuid($"{turnId:N}:{string.Join(',', callIds)}").ToString("D")
            : Guid.TryParse(message.MessageId, out _) ? message.MessageId
            : Guid.CreateVersion7().ToString("D");
        return WriteAsync(stable, stepIndex, replaceStreamed: true, cancellationToken);
    }

    private static Guid CreateDeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// 用 SDK 给出的完整消息校准投影：先按适配器整理，再逐条写入；流式内容是否被替换由适配器决定。
    /// Calibrates the projection with the SDK's complete messages: prepared by the adapter, then written one by one; the adapter decides whether streamed content is replaced.
    /// </summary>
    internal async ValueTask CalibrateAsync(
        IReadOnlyList<ChatMessage> messages,
        int? stepIndex,
        CancellationToken cancellationToken
    )
    {
        foreach (var message in _adapter.MapComplete(messages))
        {
            PrepareHeader(message);
            await WriteAsync(message, stepIndex, !_adapter.StreamIsAuthoritative, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask WriteAsync(
        ChatMessage message,
        int? stepIndex,
        bool replaceStreamed,
        CancellationToken cancellationToken
    )
    {
        var written = _projection.Put(message, stepIndex ?? ExecutionScope.Current?.StepIndex, replaceStreamed);
        await ScheduleAsync(EstimateBytes(written), cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask FinishAsync(bool completed, CancellationToken cancellationToken)
    {
        if (completed && !_failed)
            _projection.Complete();
        await ScheduleAsync(1, cancellationToken).ConfigureAwait(false);
    }

    internal IEnumerable<AgentResponseUpdate> Drain()
    {
        while (_outgoing.TryDequeue(out var message))
            yield return new AgentResponseUpdate
            {
                MessageId = message.MessageId,
                Role = message.Role,
                AuthorName = message.AuthorName,
                Contents = message.Contents,
                CreatedAt = message.CreatedAt,
                AdditionalProperties = message.AdditionalProperties,
            };
    }

    /// <summary>
    /// 投影交给本 Turn 的缓冲；不属于缓冲所在对话或没有缓冲时直接写入。
    /// Hands the projection to this turn's buffer; writes directly when the buffer belongs to another conversation or there is none.
    /// </summary>
    private async ValueTask ScheduleAsync(long changedBytes, CancellationToken cancellationToken)
    {
        if (ExecutionScope.Current?.History is { } buffer && BelongsTo(buffer.Scope))
        {
            await buffer.ScheduleAsync(_scope, _projection, changedBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _directWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 排在进行中写入之后的调用发现变化已被捕获，就不再往返数据库。
            // Callers queued behind an in-flight write find their changes already captured and skip the round trip.
            var snapshots = _projection.CapturePending();
            if (snapshots.Count == 0)
                return;
            await _store.UpsertAsync(_scope, snapshots, cancellationToken).ConfigureAwait(false);
            _projection.Acknowledge(snapshots);
        }
        finally
        {
            _directWrites.Release();
        }
    }

    private bool BelongsTo(ConversationHistoryScope conversation) =>
        conversation.ProjectId == _scope.ProjectId
        && string.Equals(
            conversation.ContextId,
            ContextIdUtil.NormalizeContextId(_scope.ContextId),
            StringComparison.Ordinal
        );

    /// <summary>
    /// 为 Agent 输出补上展示信息：没有作者时记录 Agent 名称，节点会话记录节点名称；消息自带的值保持不变。
    /// Result 归一为顶层 type = result 与 resultFormat，作者保持 SDK 的值。
    /// Adds display metadata to Agent output: the Agent name when there is no author, and the node name for node sessions; values the message already carries are kept.
    /// A Result is normalized to top-level type = result and resultFormat, keeping the SDK author.
    /// </summary>
    private void PrepareHeader(ChatMessage message)
    {
        message.AdditionalProperties = message.AdditionalProperties == null ? [] : new(message.AdditionalProperties);
        if (string.IsNullOrWhiteSpace(message.AuthorName) && _agentName != null)
            message.AdditionalProperties.TryAdd("agentName", _agentName);
        if (_scope.NodeName != null)
            message.AdditionalProperties.TryAdd("nodeName", _scope.NodeName);
        if (!AgwMessageClassifier.IsResult(message))
            return;
        message.AdditionalProperties[AgwMessageClassifier.TypeKey] = AgwMessageClassifier.ResultType;
        if (_structuredResult)
            message.AdditionalProperties[AgwMessageClassifier.ResultFormatKey] = ResultFormatJson;
        else
            message.AdditionalProperties.TryAdd(AgwMessageClassifier.ResultFormatKey, ResultFormatMarkdown);
    }

    private static readonly string ResultFormatJson = JsonSerializer.SerializeToElement(ResultFormat.Json).GetString()!;

    private static readonly string ResultFormatMarkdown = JsonSerializer
        .SerializeToElement(ResultFormat.Markdown)
        .GetString()!;

    private static long EstimateBytes(ChatMessage message) =>
        256
        + message.Contents.Sum(content =>
            content switch
            {
                TextContent text => (long)Encoding.UTF8.GetByteCount(text.Text ?? ""),
                TextReasoningContent reasoning => Encoding.UTF8.GetByteCount(reasoning.Text ?? ""),
                _ => JsonSerializer.SerializeToUtf8Bytes(content).LongLength,
            }
        );
}
