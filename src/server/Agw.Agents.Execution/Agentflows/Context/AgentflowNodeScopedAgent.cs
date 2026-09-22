using System.Runtime.ExceptionServices;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Tools.HumanInteraction;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows.Context;

/// <summary>
/// <para>为 Agentflow 节点绑定指令、会话、消息归属和执行跟踪。</para>
/// <para>Binds instructions, session scope, message attribution, and execution tracing to an Agentflow node.</para>
/// </summary>
/// <remarks>
/// <para>普通节点与嵌套 Workflow 的输入输出处理不同；节点保存待完成调用 ID，并在结束或失败时完成工具消息持久化和会话保存。</para>
/// <para>Ordinary nodes and nested workflows have different input/output handling. The wrapper tracks pending call IDs and finalizes tool-message persistence and session saving on completion or failure.</para>
/// </remarks>
internal sealed class AgentflowNodeScopedAgent : DelegatingAIAgent
{
    private const string NodeNamePropertyName = "nodeName";
    private const string PendingFunctionCallIdsStateKey =
        "Agw.Agentflows.AgentflowNodeScopedAgent.PendingFunctionCallIds";

    private readonly string _nodeId;
    private readonly string _historyNodeId;
    private readonly string? _name;
    private readonly string? _messageNodeName;
    private readonly string? _instructions;
    private readonly AgentflowAgentSessionScope? _sessionScope;
    private readonly AgentflowExecutionTraceContext? _executionTraceContext;
    private readonly Guid? _agentflowId;
    private readonly string? _traceNodeId;
    private readonly Guid? _agentId;
    private readonly bool _isWorkflow;

    /// <summary>
    /// <para>创建 AgentflowNodeScopedAgent 实例并保存本包装层使用的依赖和配置。</para>
    /// <para>Initializes AgentflowNodeScopedAgent with the dependencies and configuration used by this wrapper.</para>
    /// </summary>
    /// <param name="innerAgent">
    /// <para>由当前中间件继续调用的内层 Agent。</para>
    /// <para>Inner agent invoked by this middleware.</para>
    /// </param>
    /// <param name="nodeId">
    /// <para>当前运行节点的稳定 ID。</para>
    /// <para>Stable ID of the current runtime node.</para>
    /// </param>
    /// <param name="name">
    /// <para>可选节点展示名；未提供时沿用内层 Agent 名称或节点 ID。</para>
    /// <para>Optional display name, falling back to the inner agent name or node ID.</para>
    /// </param>
    /// <param name="instructions">
    /// <para>附加到节点输入的指令；可以为空。</para>
    /// <para>Instructions added to node input; may be null.</para>
    /// </param>
    /// <param name="sessionScope">
    /// <para>负责节点会话加载和持久化的作用域；可以为空。</para>
    /// <para>Scope loading and persisting node sessions; may be null.</para>
    /// </param>
    /// <param name="executionTraceContext">
    /// <para>节点执行跟踪上下文；为空时不建立跟踪活动。</para>
    /// <para>Node execution trace context; null disables creation of tracing activities.</para>
    /// </param>
    /// <param name="agentflowId">
    /// <para>当前 Agentflow 标识，用于会话和跟踪归属。</para>
    /// <para>Current Agentflow identifier for session and trace attribution.</para>
    /// </param>
    /// <param name="traceNodeId">
    /// <para>跟踪系统使用的节点 ID。</para>
    /// <para>Node ID used by the tracing system.</para>
    /// </param>
    /// <param name="agentId">
    /// <para>用于会话、授权或跟踪归属的 Agent 标识。</para>
    /// <para>Agent identifier used for session, approval, or trace attribution.</para>
    /// </param>
    /// <param name="historyNodeId">
    /// <para>历史持久化使用的节点 ID；未指定时采用运行节点 ID。</para>
    /// <para>Node ID used by history persistence, defaulting to the runtime node ID.</para>
    /// </param>
    /// <param name="isWorkflow">
    /// <para>是否包装嵌套 Workflow，决定输入转换和输出观察的处理路径。</para>
    /// <para>Whether a nested workflow is wrapped, selecting input conversion and output-observation behavior.</para>
    /// </param>
    public AgentflowNodeScopedAgent(
        AIAgent innerAgent,
        string nodeId,
        string? name,
        string? instructions,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext = null,
        Guid? agentflowId = null,
        string? traceNodeId = null,
        Guid? agentId = null,
        string? historyNodeId = null,
        bool isWorkflow = false
    )
        : base(innerAgent)
    {
        _nodeId = nodeId;
        _historyNodeId = historyNodeId ?? nodeId;
        _name = name;
        _messageNodeName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        _instructions = instructions;
        _sessionScope = sessionScope;
        _executionTraceContext = executionTraceContext;
        _agentflowId = agentflowId;
        _traceNodeId = traceNodeId;
        _agentId = agentId;
        _isWorkflow = isWorkflow;
    }

    /// <summary>
    /// <para>当前包装节点的稳定标识。</para>
    /// <para>Stable identifier of the wrapped node.</para>
    /// </summary>
    protected override string? IdCore => _nodeId;

    /// <summary>
    /// <para>优先使用节点名称，其次使用内层 Agent 名称，最后使用节点 ID。</para>
    /// <para>Uses the node name, then the inner agent name, and finally the node ID.</para>
    /// </summary>
    public override string? Name => _name ?? InnerAgent.Name ?? _nodeId;

    /// <summary>
    /// <para>直接暴露内层 Agent 的描述。</para>
    /// <para>Exposes the inner agent's description unchanged.</para>
    /// </summary>
    public override string? Description => InnerAgent.Description;

    /// <summary>
    /// <para>准备节点会话和指令，维护调用状态及消息归属，并完成跟踪和持久化收尾。</para>
    /// <para>Prepares node sessions and instructions, maintains call state and attribution, and finalizes tracing and persistence.</para>
    /// </summary>
    /// <remarks>
    /// <para>嵌套 Workflow 使用流式路径聚合结果；普通节点执行非流式调用，并在 finally 中完成持久化和会话保存。</para>
    /// <para>Nested workflows aggregate the streaming path; ordinary nodes call the non-streaming path and finalize persistence and session saving in finally.</para>
    /// </remarks>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>已有 SDK 会话；按节点作用域规则加载、复用或创建。</para>
    /// <para>Existing SDK session, loaded, reused, or created according to node-scope rules.</para>
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
        if (_isWorkflow)
        {
            var updates = new List<AgentResponseUpdate>();
            await foreach (
                var update in RunCoreStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false)
            )
                updates.Add(update);
            return NormalizedResponseAggregation.Aggregate(updates);
        }

        options = CreateInteractionOptions(options);
        var interactionNodeId = (
            (InteractionSource)options.AdditionalProperties![HumanInteractionToolMetadata.SourceKey]!
        ).NodeId;
        // 在节点作用域中准备会话，并恢复待完成调用，确保审批继续执行沿用同一状态。
        // Prepare the node-scoped session and restore pending calls so approval continuations keep the same state.
        var scopedSession = await PrepareSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var pendingFunctionCallIds = GetPendingFunctionCallIds(scopedSession);
        var input = AgentflowMessageTransforms.ApplyInstructions(
            AgentflowMessageTransforms.CreatePortableAgentInput(messages.ToList(), pendingFunctionCallIds),
            _instructions
        );
        if (!_isWorkflow)
            input = AgentflowMessageTransforms.PrepareNodeInputs(input);
        UpdatePendingFunctionCallIds(input.SelectMany(message => message.Contents), pendingFunctionCallIds);
        SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
        using var activity = StartExecutionActivity(input);
        var turnPersistence = new ToolTurnPersistence(InnerAgent, scopedSession, PersistToolBlockMessagesAsync);
        Exception? executionFailure = null;
        try
        {
            await ObserveInputsAsync(input, cancellationToken).ConfigureAwait(false);
            var response = await InnerAgent
                .RunAsync(input, scopedSession, options, cancellationToken)
                .ConfigureAwait(false);
            AddNodeAttribution(response.Messages, interactionNodeId);
            turnPersistence.RecordRange(response.Messages);
            UpdatePendingFunctionCallIds(
                response.Messages.SelectMany(message => message.Contents),
                pendingFunctionCallIds
            );
            SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
            var snapshots = await turnPersistence.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var snapshot in snapshots)
            {
                AddNodeAttribution(snapshot, interactionNodeId);
                response.Messages.Add(snapshot);
            }
            activity?.Complete();
            return response;
        }
        catch (OperationCanceledException exception)
        {
            executionFailure = exception;
            activity?.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            activity?.Fail(exception);
            throw;
        }
        finally
        {
            await FinalizeTurnAsync(turnPersistence, scopedSession, activity, executionFailure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>准备节点会话和指令，维护调用状态及消息归属，并完成跟踪和持久化收尾。</para>
    /// <para>Prepares node sessions and instructions, maintains call state and attribution, and finalizes tracing and persistence.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="session">
    /// <para>已有 SDK 会话；按节点作用域规则加载、复用或创建。</para>
    /// <para>Existing SDK session, loaded, reused, or created according to node-scope rules.</para>
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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        options = CreateInteractionOptions(options);
        var interactionNodeId = (
            (InteractionSource)options.AdditionalProperties![HumanInteractionToolMetadata.SourceKey]!
        ).NodeId;
        // 在节点作用域中准备会话，并恢复待完成调用，确保审批继续执行沿用同一状态。
        // Prepare the node-scoped session and restore pending calls so approval continuations keep the same state.
        var scopedSession = await PrepareSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var pendingFunctionCallIds = GetPendingFunctionCallIds(scopedSession);
        var input = AgentflowMessageTransforms.ApplyInstructions(
            AgentflowMessageTransforms.CreatePortableAgentInput(messages.ToList(), pendingFunctionCallIds),
            _instructions
        );
        if (!_isWorkflow)
            input = AgentflowMessageTransforms.PrepareNodeInputs(input);
        UpdatePendingFunctionCallIds(input.SelectMany(message => message.Contents), pendingFunctionCallIds);
        SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
        using var activity = StartExecutionActivity(input);
        var turnPersistence = new ToolTurnPersistence(InnerAgent, scopedSession, PersistToolBlockMessagesAsync);
        Exception? executionFailure = null;
        var observedCalls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        var normalizedResponse = new NormalizedResponseAggregation.Accumulator();
        try
        {
            await ObserveInputsAsync(input, cancellationToken).ConfigureAwait(false);
            await using var enumerator = InnerAgent
                .RunStreamingAsync(input, scopedSession, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                AgentResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = ProjectWorkflowObservation(enumerator.Current, observedCalls);
                    AddNodeAttribution(update, interactionNodeId);
                    var responseMessage = ToolStateSnapshots.ToMessage(update);
                    turnPersistence.Record(responseMessage);

                    UpdatePendingFunctionCallIds(update.Contents, pendingFunctionCallIds);
                    SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
                }
                catch (OperationCanceledException exception)
                {
                    executionFailure = exception;
                    activity?.Cancel();
                    throw;
                }
                catch (Exception exception)
                {
                    executionFailure = exception;
                    activity?.Fail(exception);
                    throw;
                }

                // 没有观察通道时按原路下发，规范化更新不能因此丢失。
                // Without an observation channel the update travels the ordinary path: routing it
                // nowhere would silently drop the live response.
                if (
                    NormalizedResponseAggregation.IsNormalized(update.AdditionalProperties)
                    && _sessionScope?.OutputObserver is { } observer
                )
                {
                    normalizedResponse.Add(update);
                    await observer(update, cancellationToken).ConfigureAwait(false);
                }
                else
                    yield return update;
            }

            // The framework only understands concatenating updates. Give it one final snapshot per message;
            // normalized live updates have already reached the runner's observation channel.
            foreach (var message in normalizedResponse.ReadMessages())
                yield return new AgentResponseUpdate
                {
                    MessageId = message.MessageId,
                    Role = message.Role,
                    AuthorName = message.AuthorName,
                    Contents = message.Contents,
                    CreatedAt = message.CreatedAt,
                    AdditionalProperties = message.AdditionalProperties,
                };

            var stateSnapshots = await turnPersistence.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var stateSnapshot in stateSnapshots)
            {
                var stateSnapshotUpdate = ToolStateSnapshots.ToUpdate(stateSnapshot);
                AddNodeAttribution(stateSnapshotUpdate, interactionNodeId);
                yield return stateSnapshotUpdate;
            }

            activity?.Complete();
        }
        finally
        {
            await FinalizeTurnAsync(turnPersistence, scopedSession, activity, executionFailure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>仅对普通节点中可持久化、可进入模型历史且非 handoff 的外部文本或媒体输入通知观察器。</para>
    /// <para>Notifies the observer only for ordinary-node external text or media inputs that are persistable, model-history eligible, and not handoff messages.</para>
    /// </summary>
    /// <param name="input">
    /// <para>当前节点准备处理的输入消息。</para>
    /// <para>Input messages being prepared for the current node.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private async ValueTask ObserveInputsAsync(IReadOnlyList<ChatMessage> input, CancellationToken cancellationToken)
    {
        if (_isWorkflow || _sessionScope?.InputObserver is not { } observer)
            return;
        foreach (var message in input)
        {
            if (
                AgentflowMessageTransforms.IsNodeInput(message)
                && !ConversationHistoryMetadata.IsPersistenceExcluded(message)
                && !ConversationHistoryMetadata.IsModelHistoryExcluded(message)
                && !ConversationHandoffMetadata.IsHandoffMessage(message)
                && message.Contents.All(content => content is TextContent or DataContent or UriContent)
                && message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External
            )
                await observer(message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <para>将嵌套 Workflow 已完成的调用与结果配对后暴露，保留 RequestInfoEvent 控制边界。</para>
    /// <para>Exposes completed nested-workflow calls together with their results while preserving RequestInfoEvent control boundaries.</para>
    /// </summary>
    /// <param name="update">
    /// <para>当前执行产生的响应更新。</para>
    /// <para>Response update produced by the current execution.</para>
    /// </param>
    /// <param name="observedCalls">
    /// <para>当前流中尚未配对结果的嵌套 Workflow 调用。</para>
    /// <para>Nested-workflow calls observed in the current stream and not yet paired with results.</para>
    /// </param>
    /// <returns>
    /// <para>可由父节点安全观察的更新；无需投影时返回原更新。</para>
    /// <para>Update safe for parent-node observation, or the original update when projection is unnecessary.</para>
    /// </returns>
    private AgentResponseUpdate ProjectWorkflowObservation(
        AgentResponseUpdate update,
        Dictionary<string, FunctionCallContent> observedCalls
    )
    {
        if (!_isWorkflow || update.RawRepresentation is RequestInfoEvent)
            return update;

        // 嵌套 Workflow 自己执行子调用；仅把已完成调用连同结果暴露给父级，未解决调用只能经 RequestInfoEvent 交接。
        // The nested workflow owns child calls. Expose handled calls with their results;
        // only its RequestInfoEvent may ask the parent to execute an unresolved call.
        // MAF's host collector does not honor FunctionCallContent.InformationalOnly.
        var contents = new List<AIContent>();
        foreach (var content in update.Contents)
        {
            if (content is FunctionCallContent call)
                observedCalls[call.CallId] = call;
            else if (content is FunctionResultContent result)
            {
                if (observedCalls.Remove(result.CallId, out var handledCall))
                    contents.Add(handledCall);
                contents.Add(result);
            }
            else if (content is not ToolApprovalRequestContent)
                contents.Add(content);
        }
        return new AgentResponseUpdate(update.Role, contents)
        {
            AgentId = update.AgentId,
            AuthorName = update.AuthorName,
            ResponseId = update.ResponseId,
            MessageId = update.MessageId,
            CreatedAt = update.CreatedAt,
            FinishReason = update.FinishReason,
            ContinuationToken = update.ContinuationToken,
            RawRepresentation = update.RawRepresentation,
            AdditionalProperties = update.AdditionalProperties,
        };
    }

    /// <summary>
    /// <para>复制执行选项，并把当前节点追加到父交互路径，同时写入 Provider 审批作用域。</para>
    /// <para>Clones run options, appends the current node to the parent interaction path, and records the provider approval scope.</para>
    /// </summary>
    /// <param name="options">
    /// <para>本次调用选项；为空时由后续执行层处理默认值。</para>
    /// <para>Options for this call; downstream execution handles defaults when null.</para>
    /// </param>
    /// <returns>
    /// <para>包含节点交互来源及 Provider 审批作用域的选项副本。</para>
    /// <para>Options copy containing node interaction attribution and provider approval scope.</para>
    /// </returns>
    private AgentRunOptions CreateInteractionOptions(AgentRunOptions? options)
    {
        var scoped = options?.Clone() ?? new AgentRunOptions();
        scoped.AdditionalProperties ??= [];
        var parent =
            scoped.AdditionalProperties.TryGetValue(HumanInteractionToolMetadata.SourceKey, out var value)
            && value is InteractionSource source
                ? source.NodeId
                : null;
        scoped.AdditionalProperties[HumanInteractionToolMetadata.SourceKey] = new InteractionSource
        {
            NodeId = string.IsNullOrWhiteSpace(parent) ? _nodeId : $"{parent}/{_nodeId}",
            NodeName = _name,
            ProviderScopeId = MafApprovalAdapter.GetWorkflowRequestScope(this),
        };
        return scoped;
    }

    /// <summary>
    /// <para>尝试完成工具消息持久化并保存会话；没有原始执行异常时才传播收尾错误。</para>
    /// <para>Attempts tool-message finalization and session saving, propagating cleanup failures only when no execution failure already exists.</para>
    /// </summary>
    /// <param name="turnPersistence">
    /// <para>负责本轮工具消息和状态快照写入的收尾对象。</para>
    /// <para>Finalizer responsible for this turn's tool-message and state-snapshot persistence.</para>
    /// </param>
    /// <param name="session">
    /// <para>当前 SDK 会话及其状态。</para>
    /// <para>Current SDK session and its state.</para>
    /// </param>
    /// <param name="activity">
    /// <para>当前节点跟踪活动；没有启用跟踪时为空。</para>
    /// <para>Current node tracing activity; null when tracing is unavailable.</para>
    /// </param>
    /// <param name="executionFailure">
    /// <para>已捕获的执行异常；非空时清理失败不能覆盖它。</para>
    /// <para>Previously captured execution failure; cleanup errors must not mask it when present.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private async Task FinalizeTurnAsync(
        ToolTurnPersistence turnPersistence,
        AgentSession session,
        AgentflowNodeExecutionActivityScope? activity,
        Exception? executionFailure
    )
    {
        // 分别尝试工具消息收尾和会话保存；一个失败不能阻止另一个清理步骤。
        // Attempt tool-message finalization and session saving independently so one failure does not skip the other.
        Exception? cleanupFailure = null;
        if (!turnPersistence.CompletionAttempted)
        {
            try
            {
                await turnPersistence.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        try
        {
            await SaveSessionAsync(session).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanupFailure = cleanupFailure == null ? exception : new AggregateException(cleanupFailure, exception);
        }

        if (cleanupFailure == null)
        {
            return;
        }

        activity?.Fail(cleanupFailure);
        // 原执行已经失败时保留它；仅在没有原异常时传播聚合后的收尾错误。
        // Preserve an existing execution failure; propagate collected cleanup errors only when no execution failure exists.
        if (executionFailure == null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    /// <summary>
    /// <para>仅在跟踪上下文和节点标识完整时创建节点执行活动。</para>
    /// <para>Creates a node execution activity only when tracing context and node identifiers are available.</para>
    /// </summary>
    /// <param name="input">
    /// <para>当前节点准备处理的输入消息。</para>
    /// <para>Input messages being prepared for the current node.</para>
    /// </param>
    /// <returns>
    /// <para>节点跟踪活动；缺少必要跟踪信息时为空。</para>
    /// <para>Node tracing activity, or null when required trace metadata is missing.</para>
    /// </returns>
    private AgentflowNodeExecutionActivityScope? StartExecutionActivity(IReadOnlyList<ChatMessage> input)
    {
        if (_executionTraceContext == null || !_agentflowId.HasValue || string.IsNullOrWhiteSpace(_traceNodeId))
        {
            return null;
        }

        return AgentflowNodeExecutionActivity.StartAgent(
            _executionTraceContext,
            _agentflowId.Value,
            _traceNodeId,
            _name,
            _agentId,
            InnerAgent.Name,
            input
        );
    }

    /// <summary>
    /// <para>优先从节点作用域加载或创建会话；缺少持久作用域时复用传入会话或向内层申请新会话。</para>
    /// <para>Loads or creates a session through the node scope when available, otherwise reusing the supplied session or creating one through the inner agent.</para>
    /// </summary>
    /// <param name="session">
    /// <para>已有 SDK 会话；按节点作用域规则加载、复用或创建。</para>
    /// <para>Existing SDK session, loaded, reused, or created according to node-scope rules.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>已加载、复用或创建并完成作用域初始化的节点会话。</para>
    /// <para>Loaded, reused, or newly created node session with scope initialization applied.</para>
    /// </returns>
    private async Task<AgentSession> PrepareSessionAsync(AgentSession? session, CancellationToken cancellationToken)
    {
        if (_sessionScope != null && _agentId.HasValue)
        {
            return await _sessionScope
                .GetOrCreateAsync(
                    InnerAgent,
                    _agentId.Value,
                    _agentflowId,
                    _historyNodeId,
                    _messageNodeName,
                    session,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        AgentSession scopedSession =
            session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        _sessionScope?.Initialize(scopedSession, _agentflowId, _historyNodeId, _messageNodeName);
        return scopedSession;
    }

    /// <summary>
    /// <para>存在节点会话作用域和 Agent ID 时，以不可取消令牌保存会话状态。</para>
    /// <para>Saves session state with a non-cancellable token when a node session scope and agent ID are available.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话及其状态。</para>
    /// <para>Current SDK session and its state.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private Task SaveSessionAsync(AgentSession session)
    {
        return _sessionScope != null && _agentId.HasValue
            ? _sessionScope.SaveAsync(
                InnerAgent,
                session,
                _agentId.Value,
                _agentflowId,
                _historyNodeId,
                CancellationToken.None
            )
            : Task.CompletedTask;
    }

    /// <summary>
    /// <para>补齐工具消息的节点归属，并委托节点会话作用域持久化。</para>
    /// <para>Adds node attribution to tool messages and delegates persistence to the node session scope.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>表示上述异步处理完成的任务。</para>
    /// <para>Task representing completion of the asynchronous operation described above.</para>
    /// </returns>
    private Task PersistToolBlockMessagesAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        AddNodeAttribution(messages);
        return _sessionScope?.PersistToolBlockMessagesAsync(messages, cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// <para>按需补齐节点名称和交互节点标识，保留消息上已有的归属属性。</para>
    /// <para>Adds missing node names and interaction-node identifiers while preserving attribution already present on messages.</para>
    /// </summary>
    /// <param name="messages">
    /// <para>按调用顺序提供的聊天消息。</para>
    /// <para>Chat messages in invocation order.</para>
    /// </param>
    /// <param name="interactionNodeId">
    /// <para>需要补充的交互节点路径；为空时不添加该属性。</para>
    /// <para>Interaction-node path to add; null leaves that property unchanged.</para>
    /// </param>
    private void AddNodeAttribution(IEnumerable<ChatMessage> messages, string? interactionNodeId = null)
    {
        foreach (var message in messages)
        {
            AddNodeAttribution(message, interactionNodeId);
        }
    }

    /// <summary>
    /// <para>按需补齐节点名称和交互节点标识，保留消息上已有的归属属性。</para>
    /// <para>Adds missing node names and interaction-node identifiers while preserving attribution already present on messages.</para>
    /// </summary>
    /// <param name="message">
    /// <para>待检查、复制或补充元数据的消息。</para>
    /// <para>Message to inspect, copy, or annotate.</para>
    /// </param>
    /// <param name="interactionNodeId">
    /// <para>需要补充的交互节点路径；为空时不添加该属性。</para>
    /// <para>Interaction-node path to add; null leaves that property unchanged.</para>
    /// </param>
    private void AddNodeAttribution(ChatMessage message, string? interactionNodeId = null)
    {
        if (
            (_messageNodeName == null || message.AdditionalProperties?.ContainsKey(NodeNamePropertyName) == true)
            && (interactionNodeId == null || message.AdditionalProperties?.ContainsKey("interactionNodeId") == true)
        )
        {
            return;
        }

        message.AdditionalProperties = CreateNodeProperties(message.AdditionalProperties, interactionNodeId);
    }

    /// <summary>
    /// <para>按需补齐节点名称和交互节点标识，保留消息上已有的归属属性。</para>
    /// <para>Adds missing node names and interaction-node identifiers while preserving attribution already present on messages.</para>
    /// </summary>
    /// <param name="update">
    /// <para>当前执行产生的响应更新。</para>
    /// <para>Response update produced by the current execution.</para>
    /// </param>
    /// <param name="interactionNodeId">
    /// <para>需要补充的交互节点路径；为空时不添加该属性。</para>
    /// <para>Interaction-node path to add; null leaves that property unchanged.</para>
    /// </param>
    private void AddNodeAttribution(AgentResponseUpdate update, string? interactionNodeId = null)
    {
        if (
            (_messageNodeName == null || update.AdditionalProperties?.ContainsKey(NodeNamePropertyName) == true)
            && (interactionNodeId == null || update.AdditionalProperties?.ContainsKey("interactionNodeId") == true)
        )
        {
            return;
        }

        update.AdditionalProperties = CreateNodeProperties(update.AdditionalProperties, interactionNodeId);
    }

    /// <summary>
    /// <para>复制属性字典并仅补充缺少的节点名称和交互路径，避免修改共享元数据。</para>
    /// <para>Copies the property dictionary and adds only missing node names and interaction paths, avoiding mutation of shared metadata.</para>
    /// </summary>
    /// <param name="properties">
    /// <para>已有消息属性；复制后保留其全部原值。</para>
    /// <para>Existing message properties, copied with all original values retained.</para>
    /// </param>
    /// <param name="interactionNodeId">
    /// <para>需要补充的交互节点路径；为空时不添加该属性。</para>
    /// <para>Interaction-node path to add; null leaves that property unchanged.</para>
    /// </param>
    /// <returns>
    /// <para>保留已有值并补齐节点归属的独立属性字典。</para>
    /// <para>Independent property dictionary retaining existing values and adding missing node attribution.</para>
    /// </returns>
    private AdditionalPropertiesDictionary CreateNodeProperties(
        AdditionalPropertiesDictionary? properties,
        string? interactionNodeId
    )
    {
        var result =
            properties == null ? new AdditionalPropertiesDictionary() : new AdditionalPropertiesDictionary(properties);
        if (_messageNodeName is not null)
            result.TryAdd(NodeNamePropertyName, _messageNodeName);
        if (interactionNodeId is not null)
            result.TryAdd("interactionNodeId", interactionNodeId);
        return result;
    }

    /// <summary>
    /// <para>从会话读取待完成调用 ID 并返回独立副本，缺少状态时返回空集合。</para>
    /// <para>Reads pending call IDs from the session and returns an independent copy, or an empty set when absent.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话及其状态。</para>
    /// <para>Current SDK session and its state.</para>
    /// </param>
    /// <returns>
    /// <para>使用区分大小写比较的待完成调用 ID 副本。</para>
    /// <para>Copy of pending call IDs using case-sensitive comparison.</para>
    /// </returns>
    private static HashSet<string> GetPendingFunctionCallIds(AgentSession session)
    {
        return
            session.StateBag.TryGetValue<HashSet<string>>(
                PendingFunctionCallIdsStateKey,
                out var pendingFunctionCallIds
            )
            && pendingFunctionCallIds != null
            ? new HashSet<string>(pendingFunctionCallIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// <para>以副本形式保存待完成调用 ID，避免后续原地修改影响会话快照。</para>
    /// <para>Stores a copy of pending call IDs so later in-place changes cannot mutate the session snapshot.</para>
    /// </summary>
    /// <param name="session">
    /// <para>当前 SDK 会话及其状态。</para>
    /// <para>Current SDK session and its state.</para>
    /// </param>
    /// <param name="pendingFunctionCallIds">
    /// <para>尚未收到结果的调用 ID 集合。</para>
    /// <para>Set of call IDs whose results have not yet arrived.</para>
    /// </param>
    private static void SavePendingFunctionCallIds(AgentSession session, HashSet<string> pendingFunctionCallIds)
    {
        session.StateBag.SetValue(
            PendingFunctionCallIdsStateKey,
            new HashSet<string>(pendingFunctionCallIds, StringComparer.Ordinal)
        );
    }

    /// <summary>
    /// <para>按内容顺序加入函数调用 ID，并在遇到对应结果时移除。</para>
    /// <para>Adds function-call IDs in content order and removes them when matching results appear.</para>
    /// </summary>
    /// <param name="contents">
    /// <para>按原始顺序提供的消息内容项。</para>
    /// <para>Message content items in their original order.</para>
    /// </param>
    /// <param name="pendingFunctionCallIds">
    /// <para>尚未收到结果的调用 ID 集合。</para>
    /// <para>Set of call IDs whose results have not yet arrived.</para>
    /// </param>
    private static void UpdatePendingFunctionCallIds(
        IEnumerable<Microsoft.Extensions.AI.AIContent> contents,
        HashSet<string> pendingFunctionCallIds
    )
    {
        foreach (var content in contents)
        {
            if (content is Microsoft.Extensions.AI.FunctionCallContent functionCall)
            {
                pendingFunctionCallIds.Add(functionCall.CallId);
            }
            else if (content is Microsoft.Extensions.AI.FunctionResultContent functionResult)
            {
                pendingFunctionCallIds.Remove(functionResult.CallId);
            }
        }
    }
}
