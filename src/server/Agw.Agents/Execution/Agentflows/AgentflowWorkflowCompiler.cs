using System.Text.Json;
using System.Text.Json.Serialization;

using Agw.Agents.Execution.Agentflows.Builders;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Summaries;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows;

internal sealed record AgentflowAgentSessionScope(
    IProviderSessionState ProviderSessionState,
    Guid ProjectId,
    string ContextId,
    Guid? TaskId)
{
    public void Initialize(AgentSession session)
    {
        ProviderSessionState.InitializeSessionState(
            session,
            ContextId,
            ProjectId);
    }
}

internal sealed record AgentflowSummaryContext(
    IAgentTurnSummaryService SummaryService,
    Guid ModelProviderId,
    Guid ProjectId,
    string ContextId);

public sealed class AgentflowWorkflowCompiler
{
    private const string GeneratedStartNodeId = "__agw_start";
    private const string GeneratedOutputNodeId = "__agw_output";
    private const string HumanGateOutputSuffix = "__agw_human_gate_output";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly AIAgentHostOptions AgentHostOptions = new()
    {
        EmitAgentResponseEvents = true,
        EmitAgentUpdateEvents = true,
        // 避免转发前面所有节点的输出
        ForwardIncomingMessages = false,
        ReassignOtherAgentsAsUsers = true,
    };

    private static readonly ExecutorOptions ChatExecutorOptions = ExecutorOptions.Default;

    public Workflow? Compile(
        Agentflow agentflow,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent)
    {
        return Compile(
            agentflow,
            orderedNodes,
            edges,
            nodeIdToAgent,
            sessionScope: null,
            executionTraceContext: null,
            summaryContext: null);
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
        AgentflowSummaryContext? summaryContext = null)
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
        var humanGateOutputBindings = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);

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
                summaryContext);
            if (binding != null)
            {
                bindings[node.NodeId] = binding;
                if (node.Kind == AgentflowNodeKind.HumanGate)
                {
                    humanGateOutputBindings[node.NodeId] =
                        BindChatTransform($"{node.NodeId}.{HumanGateOutputSuffix}", messages => messages);
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
        var roots = bindings.Keys
            .Where(nodeId => runtimeEdges.All(edge => edge.TargetNodeId != nodeId))
            .Select(nodeId => bindings[nodeId])
            .ToList();
        if (roots.Count == 0)
        {
            return null;
        }

        var start = roots.Count == 1
            ? roots[0]
            : BindChatTransform(GeneratedStartNodeId, messages => messages);
        var builder = new WorkflowBuilder(start)
            .WithName(agentflow.Name)
            .WithDescription(agentflow.Description ?? string.Empty)
            .WithOpenTelemetry(options => options.EnableSensitiveData = false);

        if (roots.Count > 1)
        {
            builder.AddFanOutEdge(start, roots, "start");
        }

        AddHumanGateOutputEdges(builder, bindings, humanGateOutputBindings);
        AddRuntimeEdges(builder, runtimeEdges, bindings, humanGateOutputBindings);
        AddWorkflowOutputs(builder, orderedNodes, runtimeEdges, bindings, humanGateOutputBindings);

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
        AgentflowSummaryContext? summaryContext)
    {
        var blockBuildContext = IsBlockNode(node.Kind)
            ? new AgentflowBlockBuildContext(
                agentflowId,
                node,
                nodeMap,
                nodeIdToAgent,
                sessionScope,
                executionTraceContext,
                AgentHostOptions)
            : null;

        return node.Kind switch
        {
            AgentflowNodeKind.Agent =>
                nodeIdToAgent.TryGetValue(node.NodeId, out var agent)
                    ? new AgentflowNodeScopedAgent(
                            agent,
                            node.NodeId,
                            node.Name,
                            node.Instructions,
                            sessionScope,
                            executionTraceContext,
                            agentflowId,
                            node.NodeId,
                            node.RelateId)
                        .BindAsExecutor(AgentHostOptions)
                    : null,
            AgentflowNodeKind.WorkflowAsAgent =>
                nodeIdToAgent.TryGetValue(node.NodeId, out var workflowAgent)
                    ? new AgentflowNodeScopedAgent(
                            workflowAgent,
                            node.NodeId,
                            node.Name,
                            node.Instructions,
                            sessionScope)
                        .BindAsExecutor(AgentHostOptions)
                    : null,
            AgentflowNodeKind.PromptAdapter =>
                BindChatTransform(
                    node.NodeId,
                    messages => AgentflowMessageTransforms.ApplyInstructions(messages, node.Instructions)),
            AgentflowNodeKind.CheckpointMarker =>
                BindChatTransform(node.NodeId, messages => messages),
            AgentflowNodeKind.Input =>
                new InputPassthroughAgent(node.NodeId, node.Name).BindAsExecutor(AgentHostOptions),
            AgentflowNodeKind.HumanGate =>
                RequestPort.Create<List<ChatMessage>, List<ChatMessage>>(node.NodeId).BindAsExecutor(),
            AgentflowNodeKind.ConcurrentBlock =>
                ConcurrentBlockBuilder.Build(blockBuildContext!),
            AgentflowNodeKind.GroupChatBlock =>
                GroupChatBlockBuilder.Build(blockBuildContext!),
            AgentflowNodeKind.HandoffBlock =>
                HandoffBlockBuilder.Build(blockBuildContext!),
            AgentflowNodeKind.MagenticBlock =>
                MagenticBlockBuilder.Build(blockBuildContext!),
            AgentflowNodeKind.Output =>
                CreateOutputBinding(node, summaryContext),
            _ => null,
        };
    }

    private static ExecutorBinding CreateOutputBinding(
        AgentflowNode node,
        AgentflowSummaryContext? summaryContext)
    {
        if (summaryContext == null ||
            !AgentflowDomainService.TryReadOutputSummaryEnabled(node.ConfigJson, out var summaryEnabled) ||
            !summaryEnabled)
        {
            return BindChatTransform(node.NodeId, messages => messages);
        }

        Func<List<ChatMessage>, CancellationToken, ValueTask<List<ChatMessage>>> summarizeAsync =
            async (messages, cancellationToken) =>
            {
                var result = await summaryContext.SummaryService.CreateResultAsync(
                    summaryContext.ModelProviderId,
                    messages,
                    summaryContext.ProjectId,
                    summaryContext.ContextId,
                    node.Instructions,
                    cancellationToken)
                    .ConfigureAwait(false);
                var output = messages.ToList();
                output.Add(result);
                return output;
            };

        return summarizeAsync.BindAsExecutor<List<ChatMessage>, List<ChatMessage>>(
            node.NodeId,
            ChatExecutorOptions,
            threadsafe: true);
    }

    private static void AddRuntimeEdges(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> humanGateOutputBindings)
    {
        foreach (var edge in edges.Where(edge => edge.Kind == AgentflowEdgeKind.Direct))
        {
            var source = GetSourceBinding(edge.SourceNodeId, bindings, humanGateOutputBindings);
            var target = bindings[edge.TargetNodeId];
            var label = edge.Label ?? edge.EdgeId;
            var condition = BuildCondition(edge.ConditionJson);
            if (condition == null)
            {
                builder.AddEdge(source, target, label, idempotent: true);
            }
            else
            {
                builder.AddEdge(source, target, condition, label, idempotent: true);
            }
        }

        foreach (var group in edges.Where(edge => edge.Kind == AgentflowEdgeKind.FanOut)
                     .GroupBy(edge => edge.SourceNodeId))
        {
            var source = GetSourceBinding(group.Key, bindings, humanGateOutputBindings);
            var targets = group.Select(edge => bindings[edge.TargetNodeId]).Distinct().ToList();
            if (targets.Count == 1)
            {
                builder.AddEdge(source, targets[0], group.First().Label ?? group.First().EdgeId, idempotent: true);
            }
            else if (targets.Count > 1)
            {
                builder.AddFanOutEdge(source, targets, group.First().Label ?? group.First().EdgeId);
            }
        }

        foreach (var group in edges.Where(edge => edge.Kind == AgentflowEdgeKind.FanIn)
                     .GroupBy(edge => edge.TargetNodeId))
        {
            var sources = group
                .Select(edge => GetSourceBinding(edge.SourceNodeId, bindings, humanGateOutputBindings))
                .Distinct()
                .ToList();
            if (sources.Count > 0)
            {
                builder.AddFanInBarrierEdge(sources, bindings[group.Key], group.First().Label ?? group.First().EdgeId);
            }
        }
    }

    private static void AddWorkflowOutputs(
        WorkflowBuilder builder,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyList<AgentflowEdge> runtimeEdges,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> humanGateOutputBindings)
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

        var terminalNodes = bindings.Keys
            .Where(nodeId => runtimeEdges.All(edge => edge.SourceNodeId != nodeId))
            .Select(nodeId => GetSourceBinding(nodeId, bindings, humanGateOutputBindings))
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

    private static void AddHumanGateOutputEdges(
        WorkflowBuilder builder,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> humanGateOutputBindings)
    {
        foreach (var (nodeId, outputBinding) in humanGateOutputBindings)
        {
            builder.AddEdge(bindings[nodeId], outputBinding, "human-response", idempotent: true);
        }
    }

    private static ExecutorBinding GetSourceBinding(
        string nodeId,
        IReadOnlyDictionary<string, ExecutorBinding> bindings,
        IReadOnlyDictionary<string, ExecutorBinding> humanGateOutputBindings)
    {
        return humanGateOutputBindings.TryGetValue(nodeId, out var outputBinding)
            ? outputBinding
            : bindings[nodeId];
    }

    private static Func<List<ChatMessage>?, bool>? BuildCondition(string? conditionJson)
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

            if (config.Always.HasValue)
            {
                return config.Always.Value;
            }

            var text = string.Join("\n", messages.Select(message => message.Text));
            if (!string.IsNullOrEmpty(config.Contains) &&
                !text.Contains(config.Contains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(config.NotContains) &&
                text.Contains(config.NotContains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(config.EqualsText) &&
                !string.Equals(text.Trim(), config.EqualsText, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(config.Author) &&
                messages.All(message =>
                    !string.Equals(message.AuthorName, config.Author, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(config.Role) &&
                messages.All(message =>
                    !string.Equals(message.Role.Value, config.Role, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (config.MinMessages.HasValue && messages.Count < config.MinMessages.Value)
            {
                return false;
            }

            return true;
        };
    }

    private static ExecutorBinding BindChatTransform(
        string id,
        Func<List<ChatMessage>, List<ChatMessage>> transform)
    {
        return transform.BindAsExecutor<List<ChatMessage>, List<ChatMessage>>(
            id,
            ChatExecutorOptions,
            threadsafe: true);
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
        return kind is AgentflowNodeKind.ConcurrentBlock or AgentflowNodeKind.HandoffBlock or
            AgentflowNodeKind.GroupChatBlock or AgentflowNodeKind.MagenticBlock;
    }

    private sealed record AgentflowConditionConfig
    {
        public bool? Always { get; init; }
        public string? Contains { get; init; }
        public string? NotContains { get; init; }
        [JsonPropertyName("equals")] public string? EqualsText { get; init; }
        public string? Author { get; init; }
        public string? Role { get; init; }
        public int? MinMessages { get; init; }
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
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<AgentSession>(new InputPassthroughSession());
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Messages = messages.ToList(),
                ResponseId = Guid.NewGuid().ToString("D"),
            });
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AgentResponseUpdate
                {
                    MessageId = Guid.NewGuid().ToString("D"),
                    Role = message.Role,
                    AuthorName = message.AuthorName,
                    Contents = message.Contents,
                };
            }
        }
    }

    private sealed class InputPassthroughSession : AgentSession;
}
