using System.Runtime.ExceptionServices;

using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Turns;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows;

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
        string? historyNodeId = null) : base(innerAgent)
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
        CancellationToken cancellationToken = default)
    {
        var scopedSession = await PrepareSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var pendingFunctionCallIds = GetPendingFunctionCallIds(scopedSession);
        var input = AgentflowMessageTransforms.ApplyInstructions(
            ToolApprovalSupport.RestoreWorkflowResponses(
                AgentflowMessageTransforms.CreatePortableAgentInput(
                    messages.ToList(),
                    pendingFunctionCallIds)),
            _instructions);
        UpdatePendingFunctionCallIds(input.SelectMany(message => message.Contents), pendingFunctionCallIds);
        SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
        using var activity = StartExecutionActivity(input);
        var turnPersistence = new ToolTurnPersistence(
            InnerAgent,
            scopedSession,
            PersistToolBlockMessagesAsync);
        Exception? executionFailure = null;
        try
        {
            var response = await InnerAgent
                .RunAsync(input, scopedSession, options, cancellationToken)
                .ConfigureAwait(false);
            AddNodeName(response.Messages);
            turnPersistence.RecordRange(response.Messages);
            UpdatePendingFunctionCallIds(
                response.Messages.SelectMany(message => message.Contents),
                pendingFunctionCallIds);
            SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
            var snapshots = await turnPersistence
                .CompleteAsync(CancellationToken.None)
                .ConfigureAwait(false);
            foreach (var snapshot in snapshots)
            {
                AddNodeName(snapshot);
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
            await FinalizeTurnAsync(
                    turnPersistence,
                    scopedSession,
                    activity,
                    executionFailure)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 使用节点指令和作用域会话执行流式 Agent 调用，并在流结束时记录节点执行状态。
    /// </summary>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var scopedSession = await PrepareSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var pendingFunctionCallIds = GetPendingFunctionCallIds(scopedSession);
        var input = AgentflowMessageTransforms.ApplyInstructions(
            ToolApprovalSupport.RestoreWorkflowResponses(
                AgentflowMessageTransforms.CreatePortableAgentInput(
                    messages.ToList(),
                    pendingFunctionCallIds)),
            _instructions);
        UpdatePendingFunctionCallIds(input.SelectMany(message => message.Contents), pendingFunctionCallIds);
        SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
        using var activity = StartExecutionActivity(input);
        var turnPersistence = new ToolTurnPersistence(
            InnerAgent,
            scopedSession,
            PersistToolBlockMessagesAsync);
        Exception? executionFailure = null;
        try
        {
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

                    update = enumerator.Current;
                    AddNodeName(update);
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

            var stateSnapshots = await turnPersistence
                .CompleteAsync(CancellationToken.None)
                .ConfigureAwait(false);
            foreach (var stateSnapshot in stateSnapshots)
            {
                var stateSnapshotUpdate = ToolStateSnapshots.ToUpdate(stateSnapshot);
                AddNodeName(stateSnapshotUpdate);
                yield return stateSnapshotUpdate;
            }

            activity?.Complete();
        }
        finally
        {
            await FinalizeTurnAsync(
                    turnPersistence,
                    scopedSession,
                    activity,
                    executionFailure)
                .ConfigureAwait(false);
        }
    }

    private async Task FinalizeTurnAsync(
        ToolTurnPersistence turnPersistence,
        AgentSession session,
        AgentflowNodeExecutionActivityScope? activity,
        Exception? executionFailure)
    {
        Exception? cleanupFailure = null;
        if (!turnPersistence.CompletionAttempted)
        {
            try
            {
                await turnPersistence
                    .CompleteAsync(CancellationToken.None)
                    .ConfigureAwait(false);
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
            cleanupFailure = cleanupFailure == null
                ? exception
                : new AggregateException(cleanupFailure, exception);
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
            input);
    }

    /// <summary>
    /// 获取或创建内层 Agent 会话，并将项目与 context 状态初始化到该会话。
    /// </summary>
    private async Task<AgentSession> PrepareSessionAsync(
        AgentSession? session,
        CancellationToken cancellationToken)
    {
        if (_sessionScope != null && _agentId.HasValue)
        {
            return await _sessionScope.GetOrCreateAsync(
                    InnerAgent,
                    _agentId.Value,
                    _agentflowId,
                    _historyNodeId,
                    _messageNodeName,
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        AgentSession scopedSession =
            session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        _sessionScope?.Initialize(
            scopedSession,
            _agentflowId,
            _historyNodeId,
            _messageNodeName);
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
                CancellationToken.None)
            : Task.CompletedTask;
    }

    private Task PersistToolBlockMessagesAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        AddNodeName(messages);
        return _sessionScope?.PersistToolBlockMessagesAsync(messages, cancellationToken) ??
            Task.CompletedTask;
    }

    private void AddNodeName(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            AddNodeName(message);
        }
    }

    private void AddNodeName(ChatMessage message)
    {
        if (_messageNodeName == null || message.AdditionalProperties?.ContainsKey(NodeNamePropertyName) == true)
        {
            return;
        }

        message.AdditionalProperties = CreateNodeProperties(message.AdditionalProperties);
    }

    private void AddNodeName(AgentResponseUpdate update)
    {
        if (_messageNodeName == null || update.AdditionalProperties?.ContainsKey(NodeNamePropertyName) == true)
        {
            return;
        }

        update.AdditionalProperties = CreateNodeProperties(update.AdditionalProperties);
    }

    private AdditionalPropertiesDictionary CreateNodeProperties(
        AdditionalPropertiesDictionary? properties)
    {
        var result = properties == null
            ? new AdditionalPropertiesDictionary()
            : new AdditionalPropertiesDictionary(properties);
        result[NodeNamePropertyName] = _messageNodeName;
        return result;
    }

    private static HashSet<string> GetPendingFunctionCallIds(AgentSession session)
    {
        return session.StateBag.TryGetValue<HashSet<string>>(
                PendingFunctionCallIdsStateKey,
                out var pendingFunctionCallIds) &&
            pendingFunctionCallIds != null
                ? new HashSet<string>(pendingFunctionCallIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
    }

    private static void SavePendingFunctionCallIds(
        AgentSession session,
        HashSet<string> pendingFunctionCallIds)
    {
        session.StateBag.SetValue(
            PendingFunctionCallIdsStateKey,
            new HashSet<string>(pendingFunctionCallIds, StringComparer.Ordinal));
    }

    private static void UpdatePendingFunctionCallIds(
        IEnumerable<Microsoft.Extensions.AI.AIContent> contents,
        HashSet<string> pendingFunctionCallIds)
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
