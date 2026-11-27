using System.Text.Json;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.Execution.Agentflows.Context;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agentflows.Workflows.Builders;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Summaries;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows.Workflows;

internal sealed class AgentflowAgentSessionScope
{
    public AgentflowAgentSessionScope(
        IProviderSessionState providerSessionState,
        Guid projectId,
        string contextId,
        Guid? taskId,
        AgentSessionStateStore? sessionStateStore = null,
        IConversationHistoryWriter? conversationHistoryWriter = null,
        Guid conversationId = default,
        MafPermissionState? permissionState = null
    )
    {
        ProviderSessionState = providerSessionState;
        ProjectId = projectId;
        ConversationId = conversationId;
        ContextId = contextId;
        TaskId = taskId;
        SessionStateStore = sessionStateStore;
        ConversationHistoryWriter = conversationHistoryWriter;
        PermissionState = permissionState ?? new MafPermissionState(permissionMode: null);
    }

    public IProviderSessionState ProviderSessionState { get; }

    public Guid ProjectId { get; }

    public Guid ConversationId { get; }

    public string ContextId { get; }

    public Guid? TaskId { get; }

    public MafPermissionState PermissionState { get; }

    public Func<ChatMessage, CancellationToken, ValueTask>? InputObserver { get; set; }

    private AgentSessionStateStore? SessionStateStore { get; }

    private IConversationHistoryWriter? ConversationHistoryWriter { get; }

    public void Initialize(AgentSession session, Guid? agentflowId, string nodeId, string? nodeName)
    {
        if (agentflowId.HasValue)
        {
            ProviderSessionState.InitializeSessionState(
                session,
                ContextId,
                ProjectId,
                $"agentflow:{agentflowId.Value:N}:node:{nodeId}",
                nodeName
            );
        }
        else
        {
            ProviderSessionState.InitializeSessionState(session, ContextId, ProjectId);
        }
    }

    public async Task<AgentSession> GetOrCreateAsync(
        AIAgent aiAgent,
        Guid agentId,
        Guid? agentflowId,
        string nodeId,
        string? nodeName,
        AgentSession? fallbackSession,
        CancellationToken cancellationToken
    )
    {
        AgentSession session;
        if (SessionStateStore == null)
        {
            session = fallbackSession ?? await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            session = await SessionStateStore
                .GetOrCreateForNodeAsync(aiAgent, CreateStateScope(agentId, agentflowId, nodeId), cancellationToken)
                .ConfigureAwait(false);
        }

        Initialize(session, agentflowId, nodeId, nodeName);
        PermissionState.Register(session);
        return session;
    }

    public Task SaveAsync(
        AIAgent aiAgent,
        AgentSession session,
        Guid agentId,
        Guid? agentflowId,
        string nodeId,
        CancellationToken cancellationToken
    )
    {
        return SessionStateStore?.SaveForNodeAsync(
                CreateStateScope(agentId, agentflowId, nodeId),
                aiAgent,
                session,
                cancellationToken
            )
            ?? Task.CompletedTask;
    }

    public Task PersistToolBlockMessagesAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        return ConversationHistoryWriter == null || messages.Count == 0
            ? Task.CompletedTask
            : ConversationHistoryWriter.AppendAsync(ProjectId, ContextId, messages, cancellationToken);
    }

    private AgentSessionStateScope CreateStateScope(Guid agentId, Guid? agentflowId, string nodeId) =>
        new(
            ConversationId,
            ProjectId,
            ContextId,
            agentId,
            agentflowId.HasValue ? $"{agentflowId.Value:N}:{nodeId}" : nodeId
        );
}

internal sealed record AgentflowSummaryContext(
    IAgentTurnSummaryService SummaryService,
    Guid ProjectId,
    string ContextId
);

internal sealed record AgentflowNodeResultOptions(Guid ModelProviderId, bool UseStructuredResult);

public sealed class AgentflowWorkflowCompiler
{
    private readonly ILogger<AgentflowWorkflowCompiler> _logger;

    private const string GeneratedStartNodeId = "__agw_start";
    private const string GeneratedOutputNodeId = "__agw_output";
    private const string AgentOutputSuffix = "__agw_agent_output";
    private const string HumanGateOutputSuffix = "__agw_human_gate_output";
    private const string CheckpointRequestSuffix = "__agw_checkpoint_request";
    private const string CheckpointOutputSuffix = "__agw_checkpoint_output";
    private const string RoutingBridgeSuffix = "__agw_routing_bridge";
    private const string LoopBarrierSourceSuffix = "__agw_loop_barrier_source";
    private const string LoopBarrierSuffix = "__agw_loop_barrier";

    private static readonly AIAgentHostOptions AgentHostOptions = new()
    {
        EmitAgentResponseEvents = true,
        EmitAgentUpdateEvents = true,
        InterceptUserInputRequests = false,
        // 避免转发前面所有节点的输出
        ForwardIncomingMessages = false,
        ReassignOtherAgentsAsUsers = true,
    };

    private static readonly ExecutorOptions ChatExecutorOptions = ExecutorOptions.Default;

    internal AgentflowWorkflowCompiler(ILogger<AgentflowWorkflowCompiler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    internal AgentflowCompilation? Compile(
        Agentflow agentflow,
        int agentflowRevision,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyList<AgentflowBlockMember> members,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent
    ) =>
        Compile(
            agentflow,
            agentflowRevision,
            orderedNodes,
            edges,
            members,
            nodeIdToAgent,
            sessionScope: null,
            executionTraceContext: null,
            summaryContext: null
        );

    /// <summary>
    /// 将持久化的 Agentflow 节点与边编译为可执行 Workflow，并注入会话、跟踪和摘要上下文。
    /// </summary>
    internal AgentflowCompilation? Compile(
        Agentflow agentflow,
        int agentflowRevision,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyList<AgentflowBlockMember> members,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext = null,
        AgentflowSummaryContext? summaryContext = null,
        IReadOnlyDictionary<string, EngineKind>? nodeEngineKinds = null,
        IReadOnlyDictionary<string, AgentflowNodeResultOptions>? nodeResultOptions = null
    )
    {
        if (orderedNodes.Count == 0)
        {
            return null;
        }
        nodeEngineKinds ??= new Dictionary<string, EngineKind>();
        nodeResultOptions ??= new Dictionary<string, AgentflowNodeResultOptions>();

        var nodeMap = orderedNodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var membersByNodeId = members
            .GroupBy(member => member.NodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AgentflowBlockMember>)group.OrderBy(member => member.SortOrder).ToArray(),
                StringComparer.Ordinal
            );
        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);
        var requestPortBindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);
        var sourceOutputBindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);

        foreach (var node in orderedNodes)
        {
            var binding = CreateBinding(
                agentflow.Id,
                agentflowRevision,
                node,
                membersByNodeId.GetValueOrDefault(node.NodeId) ?? [],
                nodeIdToAgent,
                nodeEngineKinds,
                nodeResultOptions,
                sessionScope,
                executionTraceContext,
                summaryContext
            );
            if (binding != null)
            {
                bindings[node.NodeId] = binding;
                if (node.Kind == AgentflowNodeKind.HumanGate)
                {
                    requestPortBindings[node.NodeId] = binding;
                    sourceOutputBindings[node.NodeId] = BindChatTransform(
                        $"{node.NodeId}.{HumanGateOutputSuffix}",
                        messages => messages
                    );
                }
                else if (node.Kind == AgentflowNodeKind.CheckpointMarker)
                {
                    requestPortBindings[node.NodeId] = RequestPort
                        .Create<List<ChatMessage>, List<ChatMessage>>(GetCheckpointRequestPortId(node.NodeId))
                        .BindAsExecutor();
                    sourceOutputBindings[node.NodeId] = BindChatRoutingBridge(
                        $"{node.NodeId}.{CheckpointOutputSuffix}"
                    );
                }
                else if (
                    node.Kind
                    is AgentflowNodeKind.Agent
                        or AgentflowNodeKind.WorkflowAsAgent
                        or AgentflowNodeKind.ConcurrentBlock
                        or AgentflowNodeKind.HandoffBlock
                        or AgentflowNodeKind.GroupChatBlock
                        or AgentflowNodeKind.MagenticBlock
                )
                {
                    // Agent 等待外部 Tool 时也会发送部分消息；收到 TurnToken 后才交给下游路由和 Output。
                    sourceOutputBindings[node.NodeId] = BindChatProtocolTransform(
                        $"{node.NodeId}.{AgentOutputSuffix}",
                        messages => messages
                    );
                }
            }
        }

        if (bindings.Count == 0)
        {
            return null;
        }

        var runtimeEdges = edges
            .Where(edge => bindings.ContainsKey(edge.SourceNodeId) && bindings.ContainsKey(edge.TargetNodeId))
            .ToList();
        var roots = bindings
            .Keys.Where(nodeId => runtimeEdges.All(edge => edge.TargetNodeId != nodeId))
            .Select(nodeId => bindings[nodeId])
            .ToList();
        if (roots.Count == 0)
        {
            return null;
        }

        var start = roots.Count == 1 ? roots[0] : BindChatTransform(GeneratedStartNodeId, messages => messages);
        var builder = new WorkflowBuilder(start)
            .WithName(agentflow.Name)
            .WithDescription(agentflow.Description ?? string.Empty)
            .WithOpenTelemetry(options => options.EnableSensitiveData = false);

        if (roots.Count > 1)
        {
            builder.AddFanOutEdge(start, roots, "start");
        }

        AddRequestPortOutputEdges(builder, nodeMap, bindings, requestPortBindings, sourceOutputBindings);
        foreach (var (nodeId, outputBinding) in sourceOutputBindings)
        {
            if (!requestPortBindings.ContainsKey(nodeId))
            {
                builder.AddEdge(bindings[nodeId], outputBinding, "agent-completed", idempotent: true);
            }
        }
        var cyclicComponents = AgentflowTopology.FindCyclicComponents(
            orderedNodes.Select(node => node.NodeId).ToList(),
            runtimeEdges
        );
        AddTypedRuntimeEdges(builder, runtimeEdges, nodeMap, bindings, sourceOutputBindings, cyclicComponents);
        AddWorkflowOutputs(builder, orderedNodes, runtimeEdges, bindings, sourceOutputBindings);

        var workflow = builder.Build(validateOrphans: false);
        var nodeIdsByExecutorId = bindings.ToDictionary(
            binding => binding.Value.Id,
            binding => binding.Key,
            StringComparer.Ordinal
        );
        return new AgentflowCompilation(workflow, nodeIdsByExecutorId);
    }

    /// <summary>
    /// 根据节点类型创建对应的 Agent、适配器、人工门、Block 或输出执行器绑定。
    /// </summary>
    private static ExecutorBinding? CreateBinding(
        Guid agentflowId,
        int agentflowRevision,
        AgentflowNode node,
        IReadOnlyList<AgentflowBlockMember> members,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        IReadOnlyDictionary<string, EngineKind> nodeEngineKinds,
        IReadOnlyDictionary<string, AgentflowNodeResultOptions> nodeResultOptions,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext,
        AgentflowSummaryContext? summaryContext
    )
    {
        var blockBuildContext = IsBlockNode(node.Kind)
            ? new AgentflowBlockBuildContext(
                agentflowId,
                agentflowRevision,
                node,
                members,
                nodeIdToAgent,
                nodeEngineKinds,
                nodeResultOptions,
                sessionScope,
                executionTraceContext,
                summaryContext,
                AgentHostOptions
            )
            : null;

        return node.Kind switch
        {
            AgentflowNodeKind.Agent => nodeIdToAgent.TryGetValue(node.NodeId, out var agent)
                ? new AgentflowNodeScopedAgent(
                    agent,
                    node.NodeId,
                    node.Name,
                    node.Instructions,
                    sessionScope,
                    executionTraceContext,
                    agentflowId: agentflowId,
                    agentflowRevision: agentflowRevision,
                    traceNodeId: node.NodeId,
                    agentId: node.RelateId,
                    engineKind: nodeEngineKinds.TryGetValue(node.NodeId, out var engineKind) ? engineKind : null,
                    resultOptions: nodeResultOptions.GetValueOrDefault(node.NodeId),
                    summaryContext: summaryContext
                ).BindAsExecutor(AgentHostOptions)
                : null,
            AgentflowNodeKind.WorkflowAsAgent => nodeIdToAgent.TryGetValue(node.NodeId, out var workflowAgent)
                ? new AgentflowNodeScopedAgent(
                    workflowAgent,
                    node.NodeId,
                    node.Name,
                    node.Instructions,
                    sessionScope,
                    agentflowId: agentflowId,
                    agentflowRevision: agentflowRevision,
                    isWorkflow: true
                ).BindAsExecutor(AgentHostOptions)
                : null,
            AgentflowNodeKind.PromptAdapter => BindChatProtocolTransform(
                node.NodeId,
                messages => AgentflowMessageTransforms.ApplyInstructions(messages, node.Instructions)
            ),
            AgentflowNodeKind.ClearMessages => BindChatProtocolTransform(node.NodeId, _ => []),
            AgentflowNodeKind.CheckpointMarker => BindCheckpointInput(node.NodeId),
            AgentflowNodeKind.Input => new InputPassthroughAgent(node.NodeId, node.Name).BindAsExecutor(
                AgentHostOptions
            ),
            AgentflowNodeKind.HumanGate => RequestPort
                .Create<List<ChatMessage>, List<ChatMessage>>(node.NodeId)
                .BindAsExecutor(),
            AgentflowNodeKind.ConcurrentBlock => ConcurrentBlockBuilder.Build(blockBuildContext!),
            AgentflowNodeKind.GroupChatBlock => GroupChatBlockBuilder.Build(blockBuildContext!),
            AgentflowNodeKind.HandoffBlock => HandoffBlockBuilder.Build(blockBuildContext!),
            AgentflowNodeKind.MagenticBlock => MagenticBlockBuilder.Build(blockBuildContext!),
            AgentflowNodeKind.Output => CreateOutputBinding(node, summaryContext),
            _ => null,
        };
    }

    private static ExecutorBinding CreateOutputBinding(AgentflowNode node, AgentflowSummaryContext? summaryContext)
    {
        if (
            summaryContext == null
            || node.SummaryModelProviderId is not { } modelProviderId
            || !AgentflowDefinitionJson.ReadOptions<OutputOptions>(node).EnableSummary
        )
        {
            return BindChatTransform(node.NodeId, messages => messages);
        }

        Func<List<ChatMessage>, CancellationToken, ValueTask<List<ChatMessage>>> summarizeAsync = async (
            messages,
            cancellationToken
        ) =>
        {
            var result = await summaryContext
                .SummaryService.CreateResultAsync(
                    modelProviderId,
                    messages,
                    summaryContext.ProjectId,
                    summaryContext.ContextId,
                    node.Instructions,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var output = messages.ToList();
            output.Add(result);
            return output;
        };

        return summarizeAsync.BindAsExecutor<List<ChatMessage>, List<ChatMessage>>(
            node.NodeId,
            ChatExecutorOptions,
            threadsafe: true
        );
    }

    private void AddTypedRuntimeEdges(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        var routableEdges = edges.Where(edge => !edge.IsFanInBarrier).ToList();
        AddDirectRuntimeEdges(builder, routableEdges, nodeMap, bindings, sourceOutputBindings, cyclicComponents);
        AddMultiRouteEdges(builder, routableEdges, nodeMap, bindings, sourceOutputBindings, cyclicComponents);
        AddSwitchRuntimeEdges(builder, routableEdges, nodeMap, bindings, sourceOutputBindings, cyclicComponents);
        AddFanInBarrierEdges(builder, edges, nodeMap, bindings, sourceOutputBindings, cyclicComponents);
    }

    private void AddDirectRuntimeEdges(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowEdge> routableEdges,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        foreach (
            var edge in routableEdges.Where(edge =>
                nodeMap[edge.SourceNodeId].RoutingStrategy == AgentflowRoutingStrategy.Direct
            )
        )
        {
            var source = GetSourceBinding(edge.SourceNodeId, bindings, sourceOutputBindings);
            var bridge = BindChatRoutingBridge(
                $"{edge.EdgeId}.{RoutingBridgeSuffix}",
                IsCyclicHumanGateAgentEdge(edge, nodeMap, cyclicComponents)
            );
            var condition = BuildEdgeCondition(edge, nodeMap[edge.SourceNodeId]);
            if (condition is null)
                builder.AddEdge(source, bridge, edge.Label ?? edge.EdgeId, idempotent: true);
            else
                builder.AddEdge(source, bridge, condition, edge.Label ?? edge.EdgeId, idempotent: true);
            builder.AddEdge(bridge, bindings[edge.TargetNodeId], edge.Label ?? edge.EdgeId, idempotent: true);
        }
    }

    private void AddMultiRouteEdges(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowEdge> routableEdges,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        foreach (
            var group in routableEdges
                .Where(edge =>
                    nodeMap[edge.SourceNodeId].RoutingStrategy
                        is AgentflowRoutingStrategy.FanOut
                            or AgentflowRoutingStrategy.Choice
                )
                .GroupBy(edge => edge.SourceNodeId)
        )
        {
            AddTypedFanOutEdges(
                builder,
                group.Key,
                group.ToList(),
                nodeMap,
                bindings,
                sourceOutputBindings,
                cyclicComponents
            );
        }
    }

    private void AddSwitchRuntimeEdges(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowEdge> routableEdges,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        foreach (
            var group in routableEdges
                .Where(edge => nodeMap[edge.SourceNodeId].RoutingStrategy == AgentflowRoutingStrategy.Switch)
                .GroupBy(edge => edge.SourceNodeId)
        )
        {
            AddTypedSwitchEdges(
                builder,
                group.Key,
                group.ToList(),
                nodeMap,
                bindings,
                sourceOutputBindings,
                cyclicComponents
            );
        }
    }

    private static void AddFanInBarrierEdges(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        foreach (var group in edges.Where(edge => edge.IsFanInBarrier).GroupBy(edge => edge.TargetNodeId))
        {
            AddFanInBarrierGroup(builder, group, nodeMap, bindings, sourceOutputBindings, cyclicComponents);
        }
    }

    private static void AddFanInBarrierGroup(
        WorkflowBuilder builder,
        IGrouping<string, AgentflowEdge> group,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        var barrierEdges = group
            .OrderBy(edge => edge.EdgeId, StringComparer.Ordinal)
            .GroupBy(edge => edge.SourceNodeId, StringComparer.Ordinal)
            .Select(sourceGroup => sourceGroup.First())
            .ToList();
        var cyclicComponent = cyclicComponents.SingleOrDefault(component => component.Contains(group.Key));
        var reusableInputEdges =
            cyclicComponent == null
                ? []
                : barrierEdges
                    .Where(edge =>
                        !cyclicComponent.Contains(edge.SourceNodeId)
                        && nodeMap[edge.SourceNodeId].Kind == AgentflowNodeKind.Input
                    )
                    .ToList();
        var repeatedEdges =
            cyclicComponent == null
                ? []
                : barrierEdges.Where(edge => cyclicComponent.Contains(edge.SourceNodeId)).ToList();

        if (
            reusableInputEdges.Count > 0
            && repeatedEdges.Count > 0
            && reusableInputEdges.Count + repeatedEdges.Count == barrierEdges.Count
        )
        {
            AddLoopBarrierEdges(builder, group.Key, reusableInputEdges, repeatedEdges, bindings, sourceOutputBindings);
            return;
        }

        AddLoopBarrierEdges(builder, group.Key, [], barrierEdges, bindings, sourceOutputBindings);
    }

    private void AddTypedFanOutEdges(
        WorkflowBuilder builder,
        string sourceNodeId,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        var orderedEdges = edges.OrderBy(edge => edge.EdgeId, StringComparer.Ordinal).ToList();
        var bridges = orderedEdges.ToDictionary(
            edge => edge.EdgeId,
            edge =>
                BindChatRoutingBridge(
                    $"{edge.EdgeId}.{RoutingBridgeSuffix}",
                    IsCyclicHumanGateAgentEdge(edge, nodeMap, cyclicComponents)
                ),
            StringComparer.Ordinal
        );
        var conditions = orderedEdges.Select(edge => BuildEdgeCondition(edge, nodeMap[sourceNodeId])).ToList();
        builder.AddFanOutEdge<List<ChatMessage>>(
            GetSourceBinding(sourceNodeId, bindings, sourceOutputBindings),
            orderedEdges.Select(edge => bridges[edge.EdgeId]).ToList(),
            (messages, targetCount) =>
                messages == null
                    ? []
                    : Enumerable
                        .Range(0, Math.Min(targetCount, conditions.Count))
                        .Where(index => conditions[index]?.Invoke(messages) ?? true),
            orderedEdges[0].Label ?? orderedEdges[0].EdgeId
        );
        foreach (var edge in orderedEdges)
        {
            builder.AddEdge(
                bridges[edge.EdgeId],
                bindings[edge.TargetNodeId],
                edge.Label ?? edge.EdgeId,
                idempotent: true
            );
        }
    }

    private void AddTypedSwitchEdges(
        WorkflowBuilder builder,
        string sourceNodeId,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        var cases = edges.Where(edge => !edge.IsSwitchDefault).OrderBy(edge => edge.SwitchCaseOrder).ToList();
        var defaultEdge = edges.SingleOrDefault(edge => edge.IsSwitchDefault);
        var switchEdges = defaultEdge is null ? cases : [.. cases, defaultEdge];
        var bridges = switchEdges.ToDictionary(
            edge => edge.EdgeId,
            edge =>
                BindChatRoutingBridge(
                    $"{edge.EdgeId}.{RoutingBridgeSuffix}",
                    IsCyclicHumanGateAgentEdge(edge, nodeMap, cyclicComponents)
                ),
            StringComparer.Ordinal
        );

        builder.AddSwitch(
            GetSourceBinding(sourceNodeId, bindings, sourceOutputBindings),
            switchBuilder =>
            {
                foreach (var edge in cases)
                {
                    if (BuildEdgeCondition(edge, nodeMap[sourceNodeId]) is { } condition)
                        switchBuilder.AddCase(condition, bridges[edge.EdgeId]);
                }

                if (defaultEdge is not null)
                    switchBuilder.WithDefault(bridges[defaultEdge.EdgeId]);
            }
        );
        foreach (var edge in switchEdges)
        {
            builder.AddEdge(
                bridges[edge.EdgeId],
                bindings[edge.TargetNodeId],
                edge.Label ?? edge.EdgeId,
                idempotent: true
            );
        }
    }

    private Func<List<ChatMessage>?, bool>? BuildEdgeCondition(AgentflowEdge edge, AgentflowNode source)
    {
        var condition = BuildCondition(AgentflowDefinitionJson.ReadConditions(edge));
        if (source.Kind != AgentflowNodeKind.HumanGate || edge.ChoiceId is not { } choiceId)
            return condition;

        return messages =>
        {
            if (messages is null)
                return false;
            var reply = messages.LastOrDefault(message =>
                string.Equals(
                    message.AuthorName,
                    AgwMessageClassifier.HumanAuthorName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            return reply?.AdditionalProperties?.TryGetValue(AgwMessageClassifier.ChoiceIdKey, out var choice) == true
                && string.Equals(choice?.ToString(), choiceId, StringComparison.Ordinal)
                && (condition?.Invoke(messages) ?? true);
        };
    }

    private static void AddLoopBarrierEdges(
        WorkflowBuilder builder,
        string targetNodeId,
        IReadOnlyList<AgentflowEdge> reusableInputEdges,
        IReadOnlyList<AgentflowEdge> repeatedEdges,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings
    )
    {
        var repeatedSourceNodeIds = repeatedEdges.Select(edge => edge.SourceNodeId).ToList();
        ExecutorBinding barrier = new LoopBarrierExecutor(
            $"{targetNodeId}.{LoopBarrierSuffix}",
            repeatedSourceNodeIds,
            reusableInputEdges.Count > 0
        );
        var barrierEdges = reusableInputEdges
            .Select(edge => (Edge: edge, ReuseAcrossIterations: true))
            .Concat(repeatedEdges.Select(edge => (Edge: edge, ReuseAcrossIterations: false)));

        foreach (var (edge, reuseAcrossIterations) in barrierEdges)
        {
            var source = GetSourceBinding(edge.SourceNodeId, bindings, sourceOutputBindings);
            var bridge = BindLoopBarrierSource(
                $"{edge.EdgeId}.{LoopBarrierSourceSuffix}",
                edge.SourceNodeId,
                reuseAcrossIterations
            );
            var label = edge.Label ?? edge.EdgeId;
            builder.AddEdge(source, bridge, label, idempotent: true);
            builder.AddEdge(bridge, barrier, label, idempotent: true);
        }

        var firstEdge = reusableInputEdges.Concat(repeatedEdges).First();
        var targetLabel = firstEdge.Label ?? firstEdge.EdgeId;
        builder.AddEdge(barrier, bindings[targetNodeId], targetLabel, idempotent: true);
    }

    private static ExecutorBinding BindChatRoutingBridge(string id, bool compactHumanFeedback = false)
    {
        return new ChatRoutingBridgeExecutor(
            id,
            compactHumanFeedback ? AgentflowMessageTransforms.CreateFeedbackLoopAgentInput : null
        );
    }

    private static ExecutorBinding BindCheckpointInput(string id) => new CheckpointInputExecutor(id);

    private static bool IsCyclicHumanGateAgentEdge(
        AgentflowEdge edge,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        if (
            nodeMap[edge.SourceNodeId].Kind != AgentflowNodeKind.HumanGate
            || nodeMap[edge.TargetNodeId].Kind != AgentflowNodeKind.Agent
        )
        {
            return false;
        }

        return cyclicComponents.Any(component =>
            component.Contains(edge.SourceNodeId) && component.Contains(edge.TargetNodeId)
        );
    }

    private static ExecutorBinding BindLoopBarrierSource(string id, string sourceNodeId, bool reuseAcrossIterations)
    {
        Func<List<ChatMessage>, LoopBarrierInput> transform = messages => new LoopBarrierInput
        {
            SourceNodeId = sourceNodeId,
            ReuseAcrossIterations = reuseAcrossIterations,
            Messages = messages.ToList(),
        };
        return transform.BindAsExecutor<List<ChatMessage>, LoopBarrierInput>(id, ChatExecutorOptions, threadsafe: true);
    }

    private static void AddWorkflowOutputs(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyList<AgentflowEdge> runtimeEdges,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings
    )
    {
        var explicitOutputs = orderedNodes
            .Where(node => node.Kind == AgentflowNodeKind.Output && bindings.ContainsKey(node.NodeId))
            .Select(node => bindings[node.NodeId])
            .ToArray();
        if (explicitOutputs.Length > 0)
        {
            builder.WithOutputFrom(explicitOutputs);
            return;
        }

        var terminalNodes = bindings
            .Keys.Where(nodeId => runtimeEdges.All(edge => edge.SourceNodeId != nodeId))
            .Select(nodeId => GetSourceBinding(nodeId, bindings, sourceOutputBindings))
            .ToList();
        if (terminalNodes.Count == 0)
        {
            return;
        }

        var output = BindChatTransform(GeneratedOutputNodeId, messages => messages);
        foreach (var terminal in terminalNodes)
        {
            builder.AddEdge(terminal, output, "output", idempotent: true);
        }

        builder.WithOutputFrom(output);
    }

    private static void AddRequestPortOutputEdges(
        WorkflowBuilder builder,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> requestPortBindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings
    )
    {
        foreach (var (nodeId, requestPortBinding) in requestPortBindings)
        {
            var outputBinding = sourceOutputBindings[nodeId];
            if (nodeMap[nodeId].Kind == AgentflowNodeKind.CheckpointMarker)
            {
                builder.AddEdge(bindings[nodeId], requestPortBinding, "checkpoint-request", idempotent: true);
            }

            var label = nodeMap[nodeId].Kind == AgentflowNodeKind.HumanGate ? "human-response" : "checkpoint-response";
            builder.AddEdge(requestPortBinding, outputBinding, label, idempotent: true);
        }
    }

    internal static string GetCheckpointRequestPortId(string nodeId) => $"{nodeId}.{CheckpointRequestSuffix}";

    private static ExecutorBinding GetSourceBinding(
        string nodeId,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> sourceOutputBindings
    )
    {
        return sourceOutputBindings.TryGetValue(nodeId, out var outputBinding) ? outputBinding : bindings[nodeId];
    }

    internal Func<List<ChatMessage>?, bool>? BuildCondition(IReadOnlyList<AgentflowConditionRule> rules)
    {
        if (rules.Count == 0)
            return null;

        return messages => messages is not null && rules.All(rule => MatchesCondition(rule, messages));
    }

    private bool MatchesCondition(AgentflowConditionRule rule, IReadOnlyList<ChatMessage> messages)
    {
        switch (rule.Field)
        {
            case "contains":
                return string.Join("\n", messages.Select(message => message.Text))
                    .Contains(rule.Value, StringComparison.OrdinalIgnoreCase);
            case "notContains":
                return !string.Join("\n", messages.Select(message => message.Text))
                    .Contains(rule.Value, StringComparison.OrdinalIgnoreCase);
            case "equals":
                return string.Equals(
                    string.Join("\n", messages.Select(message => message.Text)).Trim(),
                    rule.Value,
                    StringComparison.OrdinalIgnoreCase
                );
            case "author":
                return messages.Any(message =>
                    string.Equals(message.AuthorName, rule.Value, StringComparison.OrdinalIgnoreCase)
                );
            case "role":
                return messages.Any(message =>
                    string.Equals(message.Role.Value, rule.Value, StringComparison.OrdinalIgnoreCase)
                );
            case "minMessages":
                return int.TryParse(rule.Value, out var minimum) && messages.Count >= minimum;
            case "result":
                return MatchesResultRule(rule, messages);
            default:
                return false;
        }
    }

    private bool MatchesResultRule(AgentflowConditionRule rule, IReadOnlyList<ChatMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            var properties = message.AdditionalProperties;
            if (properties is null)
                continue;
            if (
                properties.GetValueOrDefault(AgwMessageClassifier.TypeKey)?.ToString()
                    != AgwMessageClassifier.ResultType
                || properties.GetValueOrDefault(AgwMessageClassifier.ResultFormatKey)?.ToString()
                    != AgwMessageClassifier.JsonResultFormat
            )
                continue;

            try
            {
                using var document = JsonDocument.Parse(message.Text);
                var current = document.RootElement;
                foreach (var segment in rule.Path.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var child))
                        return false;
                    current = child;
                }

                var actual = current.ValueKind switch
                {
                    JsonValueKind.String => current.GetString(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.GetRawText(),
                    _ => null,
                };
                return actual is not null && string.Equals(actual, rule.Value, StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Agentflow result condition could not parse result JSON at path {JsonPath}.",
                    rule.Path
                );
                return false;
            }
        }

        return false;
    }

    private static ExecutorBinding BindChatTransform(string id, Func<List<ChatMessage>, List<ChatMessage>> transform)
    {
        return transform.BindAsExecutor<List<ChatMessage>, List<ChatMessage>>(
            id,
            ChatExecutorOptions,
            threadsafe: true
        );
    }

    private static ExecutorBinding BindChatProtocolTransform(
        string id,
        Func<List<ChatMessage>, List<ChatMessage>> transform
    )
    {
        return new ChatTransformExecutor(id, transform);
    }

    /// <summary>
    /// 判断节点是否属于 Block。
    /// Determines whether a node is a Block.
    /// </summary>
    private static bool IsBlockNode(AgentflowNodeKind kind)
    {
        return kind
            is AgentflowNodeKind.ConcurrentBlock
                or AgentflowNodeKind.HandoffBlock
                or AgentflowNodeKind.GroupChatBlock
                or AgentflowNodeKind.MagenticBlock;
    }

    internal static string ResolveCheckpointName(AgentflowNode node)
    {
        var checkpointName = AgentflowDefinitionJson.ReadOptions<CheckpointMarkerOptions>(node).CheckpointName;
        return string.IsNullOrWhiteSpace(checkpointName)
            ? string.IsNullOrWhiteSpace(node.Name)
                ? node.NodeId
                : node.Name.Trim()
            : checkpointName.Trim();
    }

    private sealed class ChatTransformExecutor : ChatProtocolExecutor
    {
        private readonly Func<List<ChatMessage>, List<ChatMessage>> _transform;

        public ChatTransformExecutor(string id, Func<List<ChatMessage>, List<ChatMessage>> transform)
            : base(id, new ChatProtocolExecutorOptions { AutoSendTurnToken = true }, declareCrossRunShareable: true)
        {
            _transform = transform;
        }

        protected override ValueTask TakeTurnAsync(
            List<ChatMessage> messages,
            IWorkflowContext context,
            bool? emitEvents,
            CancellationToken cancellationToken = default
        )
        {
            return context.SendMessageAsync(_transform(messages), cancellationToken);
        }
    }

    private sealed class CheckpointInputExecutor : ChatProtocolExecutor
    {
        public CheckpointInputExecutor(string id)
            : base(id, new ChatProtocolExecutorOptions { AutoSendTurnToken = true }, declareCrossRunShareable: true) { }

        protected override ValueTask TakeTurnAsync(
            List<ChatMessage> messages,
            IWorkflowContext context,
            bool? emitEvents,
            CancellationToken cancellationToken = default
        )
        {
            return EndsWithToolApprovalRequest(messages)
                ? ValueTask.CompletedTask
                : context.SendMessageAsync(messages, cancellationToken);
        }

        private static bool EndsWithToolApprovalRequest(IReadOnlyList<ChatMessage> messages)
        {
            for (var messageIndex = messages.Count - 1; messageIndex >= 0; messageIndex--)
            {
                var contents = messages[messageIndex].Contents;
                for (var contentIndex = contents.Count - 1; contentIndex >= 0; contentIndex--)
                {
                    var content = contents[contentIndex];
                    if (content is Microsoft.Extensions.AI.ToolApprovalRequestContent)
                    {
                        return true;
                    }

                    if (content is Microsoft.Extensions.AI.TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                    {
                        return false;
                    }

                    if (
                        content
                        is Microsoft.Extensions.AI.FunctionResultContent
                            or Microsoft.Extensions.AI.ToolApprovalResponseContent
                    )
                    {
                        return false;
                    }
                }
            }

            return false;
        }
    }

    [SendsMessage(typeof(List<ChatMessage>))]
    [SendsMessage(typeof(TurnToken))]
    private sealed class ChatRoutingBridgeExecutor : Executor<List<ChatMessage>>
    {
        private readonly Func<List<ChatMessage>, List<ChatMessage>>? _transform;

        public ChatRoutingBridgeExecutor(string id, Func<List<ChatMessage>, List<ChatMessage>>? transform)
            : base(id, ChatExecutorOptions, declareCrossRunShareable: true)
        {
            _transform = transform;
        }

        public override async ValueTask HandleAsync(
            List<ChatMessage> messages,
            IWorkflowContext context,
            CancellationToken cancellationToken
        )
        {
            var output = _transform?.Invoke(messages) ?? messages;
            await context.SendMessageAsync(output, cancellationToken).ConfigureAwait(false);
            await context.SendMessageAsync(new TurnToken(emitEvents: true), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class LoopBarrierExecutor : StatefulExecutor<LoopBarrierState, LoopBarrierInput>
    {
        private readonly IReadOnlyList<string> _repeatedSourceNodeIds;
        private readonly bool _requiresReusableInput;

        public LoopBarrierExecutor(string id, IReadOnlyList<string> repeatedSourceNodeIds, bool requiresReusableInput)
            : base(id, () => new LoopBarrierState(), sentMessageTypes: [typeof(List<ChatMessage>), typeof(TurnToken)])
        {
            _repeatedSourceNodeIds = repeatedSourceNodeIds;
            _requiresReusableInput = requiresReusableInput;
        }

        public override async ValueTask HandleAsync(
            LoopBarrierInput input,
            IWorkflowContext context,
            CancellationToken cancellationToken
        )
        {
            var state = await ReadStateAsync(context, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (input.ReuseAcrossIterations)
            {
                state.ReusableMessages = input.Messages;
            }
            else
            {
                if (!state.PendingOutputs.TryGetValue(input.SourceNodeId, out var pendingOutputs))
                {
                    pendingOutputs = new Queue<List<ChatMessage>>();
                    state.PendingOutputs[input.SourceNodeId] = pendingOutputs;
                }

                pendingOutputs.Enqueue(input.Messages);
            }

            var completedInputs = new List<List<ChatMessage>>();
            while (
                (!_requiresReusableInput || state.ReusableMessages != null)
                && _repeatedSourceNodeIds.All(sourceId =>
                    state.PendingOutputs.TryGetValue(sourceId, out var outputs) && outputs.Count > 0
                )
            )
            {
                var messages = state.ReusableMessages?.ToList() ?? [];
                foreach (var sourceNodeId in _repeatedSourceNodeIds)
                {
                    messages.AddRange(state.PendingOutputs[sourceNodeId].Dequeue());
                }
                completedInputs.Add(messages);
            }

            await QueueStateUpdateAsync(state, context, cancellationToken).ConfigureAwait(false);
            foreach (var messages in completedInputs)
            {
                await context.SendMessageAsync(messages, cancellationToken).ConfigureAwait(false);
                await context
                    .SendMessageAsync(new TurnToken(emitEvents: true), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private sealed class LoopBarrierInput
    {
        public string SourceNodeId { get; set; } = string.Empty;

        public bool ReuseAcrossIterations { get; set; }

        public List<ChatMessage> Messages { get; set; } = [];
    }

    private sealed class LoopBarrierState
    {
        public List<ChatMessage>? ReusableMessages { get; set; }

        public Dictionary<string, Queue<List<ChatMessage>>> PendingOutputs { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class InputPassthroughAgent : AIAgent
    {
        private readonly string _nodeId;
        private readonly string? _name;

        public InputPassthroughAgent(string nodeId, string? name)
        {
            _nodeId = nodeId;
            _name = name;
        }

        protected override string? IdCore => _nodeId;

        public override string? Name => _name ?? "Input";

        public override string? Description => "User input";

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AgentSession>(new InputPassthroughSession());
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        )
        {
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        )
        {
            return ValueTask.FromResult<AgentSession>(new InputPassthroughSession());
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(
                new AgentResponse { Messages = messages.ToList(), ResponseId = Guid.CreateVersion7().ToString("D") }
            );
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 输入消息原样传给下游：受理时的输入行与执行时的输入消息共用 MessageId，handoff 等标记留在附加属性中。
                // Input messages pass downstream unchanged: the accepted input row and the execution input message share the MessageId, and markers such as handoff stay in the additional properties.
                yield return new AgentResponseUpdate
                {
                    MessageId = string.IsNullOrWhiteSpace(message.MessageId)
                        ? Guid.CreateVersion7().ToString("D")
                        : message.MessageId,
                    Role = message.Role,
                    AuthorName = message.AuthorName,
                    CreatedAt = message.CreatedAt,
                    Contents = message.Contents,
                    AdditionalProperties =
                        message.AdditionalProperties == null ? null : new(message.AdditionalProperties),
                };
            }
        }
    }

    private sealed class InputPassthroughSession : AgentSession;
}
