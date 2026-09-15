using System.Runtime.ExceptionServices;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Tools.HumanInteraction;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows.Context;

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
    /// 创建限定运行时节点标识、指令、会话作用域和执行跟踪信息的 Agent 包装器。
    /// </summary>
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

    protected override string? IdCore => _nodeId;

    public override string? Name => _name ?? InnerAgent.Name ?? _nodeId;

    public override string? Description => InnerAgent.Description;

    /// <summary>
    /// 使用节点指令和作用域会话执行非流式 Agent 调用，并记录节点执行状态。
    /// </summary>
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        if (_isWorkflow)
            return await RunCoreStreamingAsync(messages, session, options, cancellationToken)
                .ToAgentResponseAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        options = CreateInteractionOptions(options);
        var interactionNodeId = (
            (InteractionSource)options.AdditionalProperties![HumanInteractionToolMetadata.SourceKey]!
        ).NodeId;
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
    /// 使用节点指令和作用域会话执行流式 Agent 调用，并在流结束时记录节点执行状态。
    /// </summary>
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

                yield return update;
            }

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

    private AgentResponseUpdate ProjectWorkflowObservation(
        AgentResponseUpdate update,
        Dictionary<string, FunctionCallContent> observedCalls
    )
    {
        if (!_isWorkflow || update.RawRepresentation is RequestInfoEvent)
            return update;

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

    private async Task FinalizeTurnAsync(
        ToolTurnPersistence turnPersistence,
        AgentSession session,
        AgentflowNodeExecutionActivityScope? activity,
        Exception? executionFailure
    )
    {
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
        if (executionFailure == null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    /// <summary>
    /// 在执行跟踪上下文完整时启动当前节点的 Agent 执行活动。
    /// </summary>
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
    /// 获取或创建内层 Agent 会话，并将项目与 context 状态初始化到该会话。
    /// </summary>
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

    private Task PersistToolBlockMessagesAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        AddNodeAttribution(messages);
        return _sessionScope?.PersistToolBlockMessagesAsync(messages, cancellationToken) ?? Task.CompletedTask;
    }

    private void AddNodeAttribution(IEnumerable<ChatMessage> messages, string? interactionNodeId = null)
    {
        foreach (var message in messages)
        {
            AddNodeAttribution(message, interactionNodeId);
        }
    }

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

    private static void SavePendingFunctionCallIds(AgentSession session, HashSet<string> pendingFunctionCallIds)
    {
        session.StateBag.SetValue(
            PendingFunctionCallIdsStateKey,
            new HashSet<string>(pendingFunctionCallIds, StringComparer.Ordinal)
        );
    }

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
