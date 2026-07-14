using Agw.Agents.Execution.Agentflows.Observability;

using Microsoft.Agents.AI;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows;

internal sealed class AgentflowNodeScopedAgent : DelegatingAIAgent
{
    private const string PendingFunctionCallIdsStateKey =
        "Agw.Agentflows.AgentflowNodeScopedAgent.PendingFunctionCallIds";

    private readonly string _nodeId;
    private readonly string _historyNodeId;
    private readonly string? _name;
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
            AgentflowMessageTransforms.CreatePortableAgentInput(messages.ToList(), pendingFunctionCallIds),
            _instructions);
        UpdatePendingFunctionCallIds(input.SelectMany(message => message.Contents), pendingFunctionCallIds);
        SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
        using var activity = StartExecutionActivity(input);
        try
        {
            var response = await InnerAgent
                .RunAsync(input, scopedSession, options, cancellationToken)
                .ConfigureAwait(false);
            UpdatePendingFunctionCallIds(
                response.Messages.SelectMany(message => message.Contents),
                pendingFunctionCallIds);
            SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
            activity?.Complete();
            return response;
        }
        catch (OperationCanceledException)
        {
            activity?.Cancel();
            throw;
        }
        catch (Exception exception)
        {
            activity?.Fail(exception);
            throw;
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
            AgentflowMessageTransforms.CreatePortableAgentInput(messages.ToList(), pendingFunctionCallIds),
            _instructions);
        UpdatePendingFunctionCallIds(input.SelectMany(message => message.Contents), pendingFunctionCallIds);
        SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
        using var activity = StartExecutionActivity(input);
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
                UpdatePendingFunctionCallIds(update.Contents, pendingFunctionCallIds);
                SavePendingFunctionCallIds(scopedSession, pendingFunctionCallIds);
            }
            catch (OperationCanceledException)
            {
                activity?.Cancel();
                throw;
            }
            catch (Exception exception)
            {
                activity?.Fail(exception);
                throw;
            }

            yield return update;
        }

        activity?.Complete();
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
        AgentSession scopedSession =
            session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        _sessionScope?.Initialize(scopedSession, _agentflowId, _historyNodeId);
        return scopedSession;
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
