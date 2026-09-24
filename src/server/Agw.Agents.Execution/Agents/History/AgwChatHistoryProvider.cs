using System.Collections.Concurrent;
using System.Text.Json;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Turns;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// Agw 唯一的 MAF ChatHistoryProvider：调用模型前读模型历史并写入本 Step 的请求内容，调用后用完整响应校准历史投影。
/// Agw's single MAF ChatHistoryProvider: before a model call it reads model history and writes this Step's request content; after the call it calibrates the history projection with the complete response.
/// </summary>
/// <remarks>
/// System Agent 每次模型调用是一个 Step：请求里带有上一 Step 的工具结果时，先持久化这些结果并保存 StepCompleted 存档，再发出模型请求。
/// Each model call of a System Agent is one Step: when the request carries the previous Step's tool results, they are persisted and a StepCompleted checkpoint is saved before the model request is sent.
/// </remarks>
internal sealed class AgwChatHistoryProvider : ChatHistoryProvider
{
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;
    private readonly IConversationHistoryStore _store;
    private readonly HistorySessionState _sessionState;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IAgentMessageAdapter<AgentResponseUpdate>> _createAdapter;
    private readonly bool _structuredResult;
    private readonly ConcurrentDictionary<AgentSession, HistoryRecording> _recordings = new(
        ReferenceEqualityComparer.Instance
    );

    /// <summary>
    /// tracksSteps 为真时每次模型调用推进一个 Step（System Agent）；structuredResult 为真时 Result 使用 json 格式（配置了 ResponseSchema）。
    /// When tracksSteps is true every model call advances one Step (System Agents); when structuredResult is true the Result uses the json format (a ResponseSchema is configured).
    /// </summary>
    public AgwChatHistoryProvider(
        IConversationHistoryStore store,
        HistorySessionState sessionState,
        TimeProvider timeProvider,
        Func<IAgentMessageAdapter<AgentResponseUpdate>> createAdapter,
        bool tracksSteps,
        bool structuredResult
    )
    {
        _store = store;
        _sessionState = sessionState;
        _timeProvider = timeProvider;
        _createAdapter = createAdapter;
        TracksSteps = tracksSteps;
        _structuredResult = structuredResult;
    }

    /// <summary>
    /// 为一个 Agent 创建历史 Provider：适配器按 Engine 选择，System Agent 按模型调用推进 Step。
    /// Creates the history provider of one Agent: the adapter follows the Engine, and System Agents advance Steps per model call.
    /// </summary>
    internal static AgwChatHistoryProvider Create(
        IConversationHistoryStore store,
        HistorySessionState sessionState,
        TimeProvider timeProvider,
        EngineKind engine,
        bool structuredResult
    ) =>
        new(
            store,
            sessionState,
            timeProvider,
            engine switch
            {
                EngineKind.ClaudeCode => static () => new ClaudeMessageAdapter(),
                EngineKind.Codex => static () => new CodexMessageAdapter(),
                EngineKind.Pi => static () => new PiMessageAdapter(),
                _ => static () => new ModelMessageAdapter(),
            },
            tracksSteps: engine == EngineKind.Maf,
            structuredResult
        );

    internal bool TracksSteps { get; }

    public override IReadOnlyList<string> StateKeys => [_sessionState.StateKey];

    /// <summary>
    /// 为一次 Agent 运行建立历史记录；同一会话同时只能有一个运行中的记录。
    /// Starts the history recording of one Agent run; one session holds at most one running recording.
    /// </summary>
    internal HistoryRecording Begin(AIAgent agent, AgentSession session, bool transient = false)
    {
        var recording = new HistoryRecording(
            CreateWriteScope(session),
            _store,
            _createAdapter(),
            _timeProvider,
            agent.Name,
            transient,
            _structuredResult
        );
        if (!_recordings.TryAdd(session, recording))
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        return recording;
    }

    internal void End(AgentSession session) => _recordings.TryRemove(session, out _);

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken
    )
    {
        var session =
            context.Session
            ?? throw new AgwException(ErrorCodes.InvalidParam, "Agw history requires an Agent session.");
        var state = _sessionState.Get(session);
        var entries = await _store
            .ReadAsync(state.Conversation, state.HistoryScope, ExecutionScope.Current?.History, cancellationToken)
            .ConfigureAwait(false);
        // 本次请求自带的消息（例如受理时已经写入的用户输入）只从请求进入模型，历史中的同一行不再重复。
        // Messages carried by this request (such as the user input written at acceptance) reach the model only through the request, without their history row.
        var requestRowIds = context
            .RequestMessages.Select(message => Guid.TryParse(message.MessageId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var messages = entries
            .Where(entry => !requestRowIds.Contains(entry.Id))
            .Select(entry => JsonSerializer.Deserialize<ChatMessage>(entry.Payload, JsonOptions))
            .OfType<ChatMessage>()
            .Where(message => !IsExcludedFromModelHistory(message))
            .ToList();

        // 旧会话可能残留没有对应响应的工具审批请求，FunctionInvokingChatClient 会在下一轮重放历史时直接抛错。
        // 响应也可能随本轮输入提交，因此合并检查后只过滤仍未答复的请求。
        // Old sessions may keep approval requests without responses, which FunctionInvokingChatClient rejects on replay.
        // A response can arrive with this turn's input, so only requests that stay unanswered after merging are removed.
        var requestMessages = context.RequestMessages.ToList();
        var answeredCallIds = messages
            .Concat(requestMessages)
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalResponseContent>()
            .Select(content => content.ToolCall)
            .OfType<FunctionCallContent>()
            .Select(content => content.CallId)
            .ToHashSet(StringComparer.Ordinal);
        // 本次请求带来的工具结果可能已由输出流先写入历史；这些结果只从请求进入模型，历史中的副本不再重复。
        // Tool results carried by this request may already be in history from the output stream; they reach the model only through the request, without the history copy.
        var requestResultIds = requestMessages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(content => content.CallId)
            .ToHashSet(StringComparer.Ordinal);
        var answered = messages
            .Select(message => RemoveUnansweredToolApprovalRequests(message, answeredCallIds))
            .OfType<ChatMessage>()
            .Select(message => RemoveResultsCarriedByRequest(message, requestResultIds))
            .OfType<ChatMessage>()
            .ToList();
        return RemoveIncompleteFunctionCallsAndOrphanedResults(answered, requestMessages);
    }

    private static ChatMessage? RemoveResultsCarriedByRequest(ChatMessage message, IReadOnlySet<string> callIds)
    {
        if (callIds.Count == 0 || message.Role != ChatRole.Tool)
            return message;
        var contents = message
            .Contents.Where(content => content is not FunctionResultContent result || !callIds.Contains(result.CallId))
            .ToList();
        if (contents.Count == message.Contents.Count)
            return message;
        if (contents.Count == 0)
            return null;
        var filtered = message.Clone();
        filtered.Contents = contents;
        return filtered;
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        // 先读历史再写入本次请求，模型请求里同一内容只出现一次。
        // Read history before writing this request so the same content appears once in the model request.
        var messages = (await base.InvokingCoreAsync(context, cancellationToken).ConfigureAwait(false)).ToList();
        var session =
            context.Session
            ?? throw new AgwException(ErrorCodes.InvalidParam, "Agw history requires an Agent session.");
        var recording = _recordings.GetValueOrDefault(session) ?? Begin(context.Agent, session, transient: true);
        recording.MarkModelCall();
        var scope = TracksSteps ? ExecutionScope.Current : null;
        var requestMessages = context.RequestMessages.ToList();

        // 上一 Step 的工具结果紧跟它们的调用写入，并在此保存 StepCompleted。
        // The previous Step's tool results are written right after their calls, and StepCompleted is saved here.
        var toolResults = requestMessages
            .Where(message => message.Role == ChatRole.Tool && message.Contents.OfType<FunctionResultContent>().Any())
            .Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message))
            .ToList();
        if (toolResults.Count > 0)
        {
            foreach (var toolResult in toolResults)
                await recording
                    .PutToolResultAsync(toolResult, scope?.StepIndex, cancellationToken)
                    .ConfigureAwait(false);
            if (scope is { StepIndex: > 0 })
                await SaveStepCompletedAsync(scope, context, cancellationToken).ConfigureAwait(false);
        }

        // 输入与需要排在响应之前的消息属于推进之前的 Step：Turn 的原始输入是 Step 0。
        // The input and messages that must precede the response belong to the Step before advancing: the turn's original input is Step 0.
        await recording
            .WriteMessagesAsync(
                (recording.TakeStagedInput() ?? SelectRequestInputs(requestMessages)).Concat(
                    ConversationHistoryPrelude.Take(session)
                ),
                scope?.StepIndex,
                cancellationToken
            )
            .ConfigureAwait(false);
        scope?.AdvanceStep();
        return messages;
    }

    protected override async ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (context.Session == null || !_recordings.TryGetValue(context.Session, out var recording))
            return;
        try
        {
            var stepIndex = TracksSteps ? ExecutionScope.Current?.StepIndex : null;
            // 没有先调用 InvokingAsync 的 SDK 在这里写入暂存的原始输入，输入仍排在响应之前。
            // For SDKs that skip InvokingAsync the staged original input is written here, still ahead of the response.
            if (recording.TakeStagedInput() is { } input)
                await recording.WriteMessagesAsync(input, stepIndex, cancellationToken).ConfigureAwait(false);
            if (context.InvokeException == null)
            {
                // 完整响应校准增量投影；每次模型调用的步骤序号在请求前已经推进。
                // The complete response calibrates the delta projection; the Step index advanced before the request.
                await recording
                    .CalibrateAsync((context.ResponseMessages ?? []).ToList(), stepIndex, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (recording.Transient)
            {
                try
                {
                    await recording
                        .FinishAsync(context.InvokeException == null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    End(context.Session);
                }
            }
        }
    }

    /// <summary>
    /// 在已提交历史的边界保存 StepCompleted：只针对顶层 Agent Turn，节点由 Workflow 存档覆盖。
    /// Saves StepCompleted at a committed history boundary, for top-level Agent turns only; nodes are covered by Workflow checkpoints.
    /// </summary>
    private static async Task SaveStepCompletedAsync(
        ExecutionScope scope,
        InvokingContext context,
        CancellationToken cancellationToken
    )
    {
        if (
            scope.Checkpoints == null
            || scope.Context.Node != null
            || scope.Context.RuntimeType != AgentRuntimeType.Agent
            || scope.History == null
        )
            return;
        await using var barrier = await scope.History.EnterBarrierAsync(cancellationToken).ConfigureAwait(false);
        var session = await context
            .Agent.SerializeSessionAsync(context.Session!, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await scope
            .Checkpoints.SaveAsync(
                new AgentTurnCheckpoint(
                    scope.Context.TurnId,
                    scope.StepIndex,
                    TurnCheckpointKind.StepCompleted,
                    scope.Interactions.Snapshot().ApprovalRounds,
                    JsonSerializer.Serialize(session),
                    scope.History.CommittedSequence,
                    []
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (scope.Turn is { } turn)
            await turn.CompleteStepAsync(scope.StepIndex, cancellationToken).ConfigureAwait(false);
    }

    private ConversationMessageWriteScope CreateWriteScope(AgentSession session)
    {
        var scope = _sessionState.GetMessageWriteScope(session);
        return
            ExecutionScope.Current is { } execution
            && execution.ProjectId == scope.ProjectId
            && execution.ContextId == scope.ContextId
            ? scope with
            {
                TurnId = execution.Context.TurnId,
                AgentId = execution.Context.AgentId,
            }
            : scope;
    }

    /// <summary>
    /// 没有暂存的原始输入时，本次请求里来自调用方的消息就是输入；工具结果与历史来源的消息另行处理。
    /// Without a staged original input, the caller's messages of this request are the input; tool results and history-sourced messages are handled separately.
    /// </summary>
    private static IReadOnlyList<ChatMessage> SelectRequestInputs(IEnumerable<ChatMessage> messages) =>
        messages
            .Where(message => message.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory)
            .Where(message => message.Role != ChatRole.Tool)
            .Where(message => !ConversationHistoryMetadata.IsPersistenceExcluded(message))
            .ToList();

    private static bool IsExcludedFromModelHistory(ChatMessage message) =>
        ConversationHistoryMetadata.IsModelHistoryExcluded(message)
        || ConversationHistoryMetadata.IsPersistenceExcluded(message)
        || ConversationHistoryMetadata.IsUserMemoryContext(message)
        || AgwMessageClassifier.IsResult(message)
        || IsType(message, AgwMessageTypes.AgentflowCheckpoint)
        || message.AdditionalProperties.IsToolMessage();

    private static bool IsType(ChatMessage message, string type) =>
        message.AdditionalProperties?.TryGetValue("type", out var value) == true
        && string.Equals(value?.ToString(), type, StringComparison.Ordinal);

    private static ChatMessage? RemoveUnansweredToolApprovalRequests(
        ChatMessage message,
        IReadOnlySet<string> answeredCallIds
    )
    {
        var contents = message
            .Contents.Where(content =>
                content
                    is not ToolApprovalRequestContent
                    {
                        ToolCall: FunctionCallContent { InformationalOnly: false } functionCall
                    }
                || answeredCallIds.Contains(functionCall.CallId)
            )
            .ToList();
        if (contents.Count == message.Contents.Count)
            return message;
        if (contents.Count == 0)
            return null;
        var filtered = message.Clone();
        filtered.Contents = contents;
        return filtered;
    }

    /// <summary>
    /// 按出现顺序配对每次调用，结果也可以来自下一次模型请求；不成对的调用与结果不进入模型历史。
    /// Pairs individual calls in arrival order, including results in the next model request; unpaired calls and results stay out of model history.
    /// </summary>
    internal static List<ChatMessage> RemoveIncompleteFunctionCallsAndOrphanedResults(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatMessage> followingMessages
    )
    {
        var pendingCalls = new Dictionary<string, Queue<(int Message, int Content)>>(StringComparer.Ordinal);
        var pairedContents = new HashSet<(int Message, int Content)>();
        foreach (
            var (message, messageIndex) in messages
                .Concat(followingMessages)
                .Select((message, index) => (message, index))
        )
        {
            for (var contentIndex = 0; contentIndex < message.Contents.Count; contentIndex++)
            {
                var position = (messageIndex, contentIndex);
                if (message.Role == ChatRole.Assistant && message.Contents[contentIndex] is FunctionCallContent call)
                {
                    if (!pendingCalls.TryGetValue(call.CallId, out var calls))
                        pendingCalls.Add(call.CallId, calls = new());
                    calls.Enqueue(position);
                }
                else if (
                    message.Role == ChatRole.Tool
                    && message.Contents[contentIndex] is FunctionResultContent response
                    && pendingCalls.TryGetValue(response.CallId, out var calls)
                    && calls.TryDequeue(out var callPosition)
                )
                {
                    pairedContents.Add(callPosition);
                    pairedContents.Add(position);
                }
            }
        }
        var result = new List<ChatMessage>(messages.Count);
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            var contents = message
                .Contents.Where(
                    (content, contentIndex) =>
                        !(
                            message.Role == ChatRole.Assistant && content is FunctionCallContent
                            || message.Role == ChatRole.Tool && content is FunctionResultContent
                        ) || pairedContents.Contains((messageIndex, contentIndex))
                )
                .ToList();
            if (contents.Count == 0)
                continue;
            if (contents.Count == message.Contents.Count)
            {
                result.Add(message);
                continue;
            }
            var filtered = message.Clone();
            filtered.Contents = contents;
            result.Add(filtered);
        }
        return result;
    }
}
