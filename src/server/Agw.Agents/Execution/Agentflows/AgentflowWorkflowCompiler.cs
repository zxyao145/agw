using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Agents.Execution.Agentflows.Builders;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows;

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
        PermissionModeState? permissionState = null
    )
    {
        ProviderSessionState = providerSessionState;
        ProjectId = projectId;
        ConversationId = conversationId;
        ContextId = contextId;
        TaskId = taskId;
        SessionStateStore = sessionStateStore;
        ConversationHistoryWriter = conversationHistoryWriter;
        PermissionState = permissionState ?? new PermissionModeState(permissionMode: null);
    }

    public IProviderSessionState ProviderSessionState { get; }

    public Guid ProjectId { get; }

    public Guid ConversationId { get; }

    public string ContextId { get; }

    public Guid? TaskId { get; }

    public PermissionModeState PermissionState { get; }

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
    Guid ModelProviderId,
    Guid ProjectId,
    string ContextId
);

public sealed class AgentflowWorkflowCompiler
{
    private const string GeneratedStartNodeId = "__agw_start";
    private const string GeneratedOutputNodeId = "__agw_output";
    private const string HumanGateOutputSuffix = "__agw_human_gate_output";
    private const string CheckpointRequestSuffix = "__agw_checkpoint_request";
    private const string CheckpointOutputSuffix = "__agw_checkpoint_output";
    private const string RoutingBridgeSuffix = "__agw_routing_bridge";
    private const string LoopBarrierSourceSuffix = "__agw_loop_barrier_source";
    private const string LoopBarrierSuffix = "__agw_loop_barrier";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public Workflow? Compile(
        Agentflow agentflow,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent
    )
    {
        return Compile(
            agentflow,
            orderedNodes,
            edges,
            nodeIdToAgent,
            sessionScope: null,
            executionTraceContext: null,
            summaryContext: null
        );
    }

    /// <summary>
    /// 将持久化的 Agentflow 节点与边编译为可执行 Workflow，并注入会话、跟踪和摘要上下文。
    /// </summary>
    internal Workflow? Compile(
        Agentflow agentflow,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext = null,
        AgentflowSummaryContext? summaryContext = null
    )
    {
        if (orderedNodes.Count == 0)
        {
            return null;
        }

        var nodeMap = orderedNodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var blockParticipantNodeIds = GetBlockParticipantNodeIds(orderedNodes);
        var edgeNodeIds = edges
            .SelectMany(edge => new[] { edge.SourceNodeId, edge.TargetNodeId })
            .ToHashSet(StringComparer.Ordinal);
        var bindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);
        var requestPortBindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);
        var requestPortOutputBindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);

        foreach (var node in orderedNodes)
        {
            if (blockParticipantNodeIds.Contains(node.NodeId) && !edgeNodeIds.Contains(node.NodeId))
            {
                continue;
            }

            var binding = CreateBinding(
                agentflow.Id,
                node,
                nodeMap,
                nodeIdToAgent,
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
                    requestPortOutputBindings[node.NodeId] = BindChatTransform(
                        $"{node.NodeId}.{HumanGateOutputSuffix}",
                        messages => messages
                    );
                }
                else if (node.Kind == AgentflowNodeKind.CheckpointMarker)
                {
                    requestPortBindings[node.NodeId] = RequestPort
                        .Create<List<ChatMessage>, List<ChatMessage>>(GetCheckpointRequestPortId(node.NodeId))
                        .BindAsExecutor();
                    requestPortOutputBindings[node.NodeId] = BindChatRoutingBridge(
                        $"{node.NodeId}.{CheckpointOutputSuffix}"
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

        AddRequestPortOutputEdges(builder, nodeMap, bindings, requestPortBindings, requestPortOutputBindings);
        var cyclicComponents = AgentflowTopology.FindCyclicComponents(
            orderedNodes.Select(node => node.NodeId).ToList(),
            runtimeEdges
        );
        AddRuntimeEdges(builder, runtimeEdges, nodeMap, bindings, requestPortOutputBindings, cyclicComponents);
        AddWorkflowOutputs(builder, orderedNodes, runtimeEdges, bindings, requestPortOutputBindings);

        return builder.Build(validateOrphans: false);
    }

    /// <summary>
    /// 根据节点类型创建对应的 Agent、适配器、人工门、Block 或输出执行器绑定。
    /// </summary>
    private static ExecutorBinding? CreateBinding(
        Guid agentflowId,
        AgentflowNode node,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext,
        AgentflowSummaryContext? summaryContext
    )
    {
        var blockBuildContext = IsBlockNode(node.Kind)
            ? new AgentflowBlockBuildContext(
                agentflowId,
                node,
                nodeMap,
                nodeIdToAgent,
                sessionScope,
                executionTraceContext,
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
                    agentflowId,
                    node.NodeId,
                    node.RelateId
                ).BindAsExecutor(AgentHostOptions)
                : null,
            AgentflowNodeKind.WorkflowAsAgent => nodeIdToAgent.TryGetValue(node.NodeId, out var workflowAgent)
                ? new AgentflowNodeScopedAgent(
                    workflowAgent,
                    node.NodeId,
                    node.Name,
                    node.Instructions,
                    sessionScope,
                    agentflowId: agentflowId
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
            || !AgentflowTopology.TryReadOutputSummaryEnabled(node.ConfigJson, out var summaryEnabled)
            || !summaryEnabled
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
                    summaryContext.ModelProviderId,
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

    private static void AddRuntimeEdges(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> requestPortOutputBindings,
        IReadOnlyList<HashSet<string>> cyclicComponents
    )
    {
        foreach (var edge in edges.Where(edge => edge.Kind == AgentflowEdgeKind.Direct))
        {
            var source = GetSourceBinding(edge.SourceNodeId, bindings, requestPortOutputBindings);
            var target = bindings[edge.TargetNodeId];
            var label = edge.Label ?? edge.EdgeId;
            var compactHumanFeedback = IsCyclicHumanGateAgentEdge(edge, nodeMap, cyclicComponents);
            var condition = BuildCondition(
                edge.ConditionJson,
                nodeMap[edge.SourceNodeId].Kind == AgentflowNodeKind.HumanGate
            );
            if (condition == null && !compactHumanFeedback)
            {
                builder.AddEdge(source, target, label, idempotent: true);
            }
            else
            {
                var bridge = BindChatRoutingBridge($"{edge.EdgeId}.{RoutingBridgeSuffix}", compactHumanFeedback);
                if (condition == null)
                {
                    builder.AddEdge(source, bridge, label, idempotent: true);
                }
                else
                {
                    builder.AddEdge(source, bridge, condition, label, idempotent: true);
                }

                builder.AddEdge(bridge, target, label, idempotent: true);
            }
        }

        foreach (
            var group in edges.Where(edge => edge.Kind == AgentflowEdgeKind.FanOut).GroupBy(edge => edge.SourceNodeId)
        )
        {
            var source = GetSourceBinding(group.Key, bindings, requestPortOutputBindings);
            var fanOutEdges = group.OrderBy(edge => edge.EdgeId, StringComparer.Ordinal).ToList();
            var bridges = fanOutEdges.ToDictionary(
                edge => edge.EdgeId,
                edge =>
                    BindChatRoutingBridge(
                        $"{edge.EdgeId}.{RoutingBridgeSuffix}",
                        IsCyclicHumanGateAgentEdge(edge, nodeMap, cyclicComponents)
                    ),
                StringComparer.Ordinal
            );
            var targets = fanOutEdges.Select(edge => bridges[edge.EdgeId]).ToList();
            var conditions = fanOutEdges
                .Select(edge =>
                    BuildCondition(edge.ConditionJson, nodeMap[group.Key].Kind == AgentflowNodeKind.HumanGate)
                )
                .ToList();
            var label = fanOutEdges[0].Label ?? fanOutEdges[0].EdgeId;
            builder.AddFanOutEdge<List<ChatMessage>>(
                source,
                targets,
                (messages, targetCount) =>
                    messages == null
                        ? []
                        : Enumerable
                            .Range(0, Math.Min(targetCount, conditions.Count))
                            .Where(index => conditions[index]?.Invoke(messages) ?? true),
                label
            );
            foreach (var edge in fanOutEdges)
            {
                builder.AddEdge(
                    bridges[edge.EdgeId],
                    bindings[edge.TargetNodeId],
                    edge.Label ?? edge.EdgeId,
                    idempotent: true
                );
            }
        }

        foreach (
            var group in edges
                .Where(edge => edge.Kind == AgentflowEdgeKind.FanInBarrier)
                .GroupBy(edge => edge.TargetNodeId)
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
                AddLoopBarrierEdges(
                    builder,
                    group.Key,
                    reusableInputEdges,
                    repeatedEdges,
                    bindings,
                    requestPortOutputBindings
                );
                continue;
            }

            var sources = barrierEdges
                .Select(edge => GetSourceBinding(edge.SourceNodeId, bindings, requestPortOutputBindings))
                .ToList();
            if (sources.Count > 0)
            {
                var label = barrierEdges[0].Label ?? barrierEdges[0].EdgeId;
                builder.AddFanInBarrierEdge(sources, bindings[group.Key], label);
            }
        }

        foreach (
            var group in edges
                .Where(edge => edge.Kind is AgentflowEdgeKind.SwitchCase or AgentflowEdgeKind.SwitchDefault)
                .GroupBy(edge => edge.SourceNodeId)
        )
        {
            var source = GetSourceBinding(group.Key, bindings, requestPortOutputBindings);
            var cases = group
                .Where(edge => edge.Kind == AgentflowEdgeKind.SwitchCase)
                .OrderBy(edge => GetSwitchCaseOrder(edge))
                .ThenBy(edge => edge.EdgeId, StringComparer.Ordinal)
                .ToList();
            var defaultEdge = group.SingleOrDefault(edge => edge.Kind == AgentflowEdgeKind.SwitchDefault);
            var switchEdges = cases.ToList();
            if (defaultEdge != null)
            {
                switchEdges.Add(defaultEdge);
            }
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
                source,
                switchBuilder =>
                {
                    foreach (var edge in cases)
                    {
                        var condition = BuildCondition(
                            edge.ConditionJson,
                            nodeMap[group.Key].Kind == AgentflowNodeKind.HumanGate
                        );
                        if (condition != null)
                        {
                            switchBuilder.AddCase(condition, bridges[edge.EdgeId]);
                        }
                    }

                    if (defaultEdge != null)
                    {
                        switchBuilder.WithDefault(bridges[defaultEdge.EdgeId]);
                    }
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
    }

    private static void AddLoopBarrierEdges(
        WorkflowBuilder builder,
        string targetNodeId,
        IReadOnlyList<AgentflowEdge> reusableInputEdges,
        IReadOnlyList<AgentflowEdge> repeatedEdges,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> requestPortOutputBindings
    )
    {
        var repeatedSourceNodeIds = repeatedEdges.Select(edge => edge.SourceNodeId).ToList();
        ExecutorBinding barrier = new LoopBarrierExecutor($"{targetNodeId}.{LoopBarrierSuffix}", repeatedSourceNodeIds);
        var barrierEdges = reusableInputEdges
            .Select(edge => (Edge: edge, ReuseAcrossIterations: true))
            .Concat(repeatedEdges.Select(edge => (Edge: edge, ReuseAcrossIterations: false)));

        foreach (var (edge, reuseAcrossIterations) in barrierEdges)
        {
            var source = GetSourceBinding(edge.SourceNodeId, bindings, requestPortOutputBindings);
            var bridge = BindLoopBarrierSource(
                $"{edge.EdgeId}.{LoopBarrierSourceSuffix}",
                edge.SourceNodeId,
                reuseAcrossIterations
            );
            var label = edge.Label ?? edge.EdgeId;
            builder.AddEdge(source, bridge, label, idempotent: true);
            builder.AddEdge(bridge, barrier, label, idempotent: true);
        }

        var targetLabel = reusableInputEdges[0].Label ?? reusableInputEdges[0].EdgeId;
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

    private static int GetSwitchCaseOrder(AgentflowEdge edge)
    {
        return AgentflowTopology.TryReadSwitchCaseOrder(edge.ConfigJson, out var order) ? order : int.MaxValue;
    }

    private static void AddWorkflowOutputs(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyList<AgentflowEdge> runtimeEdges,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> requestPortOutputBindings
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
            .Select(nodeId => GetSourceBinding(nodeId, bindings, requestPortOutputBindings))
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
        IReadOnlyDictionary<string, ExecutorBinding> requestPortOutputBindings
    )
    {
        foreach (var (nodeId, outputBinding) in requestPortOutputBindings)
        {
            var requestPortBinding = requestPortBindings[nodeId];
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
        IReadOnlyDictionary<string, ExecutorBinding> requestPortOutputBindings
    )
    {
        return requestPortOutputBindings.TryGetValue(nodeId, out var outputBinding) ? outputBinding : bindings[nodeId];
    }

    private static Func<List<ChatMessage>?, bool>? BuildCondition(
        string? conditionJson,
        bool useLatestHumanReply = false
    )
    {
        if (string.IsNullOrWhiteSpace(conditionJson))
        {
            return null;
        }

        var config = JsonSerializer.Deserialize<AgentflowConditionConfig>(conditionJson, JsonOptions);
        if (config == null)
        {
            return null;
        }

        return messages =>
        {
            if (messages == null)
            {
                return false;
            }

            IReadOnlyList<ChatMessage> conditionMessages = messages;
            if (useLatestHumanReply)
            {
                var latestHumanReply = messages.LastOrDefault(message =>
                    string.Equals(message.AuthorName, "human", StringComparison.OrdinalIgnoreCase)
                );
                conditionMessages = latestHumanReply == null ? [] : [latestHumanReply];
            }

            if (config.Always.HasValue)
            {
                return config.Always.Value;
            }

            var text = string.Join("\n", conditionMessages.Select(message => message.Text));
            if (
                !string.IsNullOrEmpty(config.Contains)
                && !text.Contains(config.Contains, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }

            if (
                !string.IsNullOrEmpty(config.NotContains)
                && text.Contains(config.NotContains, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }

            if (
                !string.IsNullOrEmpty(config.EqualsText)
                && !string.Equals(text.Trim(), config.EqualsText, StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }

            if (
                !string.IsNullOrEmpty(config.Author)
                && conditionMessages.All(message =>
                    !string.Equals(message.AuthorName, config.Author, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return false;
            }

            if (
                !string.IsNullOrEmpty(config.Role)
                && conditionMessages.All(message =>
                    !string.Equals(message.Role.Value, config.Role, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return false;
            }

            if (config.MinMessages.HasValue && conditionMessages.Count < config.MinMessages.Value)
            {
                return false;
            }

            return true;
        };
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
    /// 收集所有 Block 配置引用的参与节点与管理节点标识。
    /// </summary>
    private static HashSet<string> GetBlockParticipantNodeIds(IReadOnlyList<AgentflowNode> nodes)
    {
        var participantNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Where(node => IsBlockNode(node.Kind)))
        {
            var config = AgentflowBlockBuildSupport.ReadConfig(node);
            foreach (var participantId in config.ParticipantNodeIds ?? [])
            {
                participantNodeIds.Add(participantId);
            }

            if (!string.IsNullOrWhiteSpace(config.ManagerNodeId))
            {
                participantNodeIds.Add(config.ManagerNodeId);
            }
        }

        return participantNodeIds;
    }

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
        if (!string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            try
            {
                using var document = JsonDocument.Parse(node.ConfigJson);
                if (
                    document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("checkpointName", out var property)
                    && property.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.GetString())
                )
                {
                    return property.GetString()!.Trim();
                }
            }
            catch (JsonException) { }
        }

        return string.IsNullOrWhiteSpace(node.Name) ? node.NodeId : node.Name.Trim();
    }

    private sealed record AgentflowConditionConfig
    {
        public bool? Always { get; init; }
        public string? Contains { get; init; }
        public string? NotContains { get; init; }

        [JsonPropertyName("equals")]
        public string? EqualsText { get; init; }
        public string? Author { get; init; }
        public string? Role { get; init; }
        public int? MinMessages { get; init; }
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

        public LoopBarrierExecutor(string id, IReadOnlyList<string> repeatedSourceNodeIds)
            : base(id, () => new LoopBarrierState(), sentMessageTypes: [typeof(List<ChatMessage>), typeof(TurnToken)])
        {
            _repeatedSourceNodeIds = repeatedSourceNodeIds;
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
                if (!state.PendingMessages.TryGetValue(input.SourceNodeId, out var pendingMessages))
                {
                    pendingMessages = [];
                    state.PendingMessages[input.SourceNodeId] = pendingMessages;
                }

                pendingMessages.AddRange(input.Messages);
            }

            var isReady =
                state.ReusableMessages != null && _repeatedSourceNodeIds.All(state.PendingMessages.ContainsKey);
            List<ChatMessage>? messages = null;
            if (isReady)
            {
                messages = state.ReusableMessages!.ToList();
                foreach (var sourceNodeId in _repeatedSourceNodeIds)
                {
                    messages.AddRange(state.PendingMessages[sourceNodeId]);
                }

                state.PendingMessages.Clear();
            }

            await QueueStateUpdateAsync(state, context, cancellationToken).ConfigureAwait(false);
            if (messages == null)
            {
                return;
            }

            await context.SendMessageAsync(messages, cancellationToken).ConfigureAwait(false);
            await context.SendMessageAsync(new TurnToken(emitEvents: true), cancellationToken).ConfigureAwait(false);
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

        public Dictionary<string, List<ChatMessage>> PendingMessages { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class InputPassthroughAgent(string nodeId, string? name) : AIAgent
    {
        protected override string? IdCore => nodeId;

        public override string? Name => name ?? "Input";

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
                yield return new AgentResponseUpdate
                {
                    MessageId = Guid.CreateVersion7().ToString("D"),
                    Role = message.Role,
                    AuthorName = message.AuthorName,
                    Contents = message.Contents,
                };
            }
        }
    }

    private sealed class InputPassthroughSession : AgentSession;
}
