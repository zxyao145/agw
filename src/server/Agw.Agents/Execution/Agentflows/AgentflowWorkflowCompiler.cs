using System.Text.Json;
using System.Text.Json.Serialization;

using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Summaries;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

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
        ReassignOtherAgentsAsUsers = false,
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
                orderedNodes,
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

    private static ExecutorBinding? CreateBinding(
        Guid agentflowId,
        AgentflowNode node,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext,
        AgentflowSummaryContext? summaryContext)
    {
        return node.Kind switch
        {
            AgentflowNodeKind.Agent =>
                nodeIdToAgent.TryGetValue(node.NodeId, out var agent)
                    ? new NodeScopedAgent(
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
                    ? new NodeScopedAgent(
                            workflowAgent,
                            node.NodeId,
                            node.Name,
                            node.Instructions,
                            sessionScope)
                        .BindAsExecutor(AgentHostOptions)
                    : null,
            AgentflowNodeKind.PromptAdapter =>
                BindChatTransform(node.NodeId, messages => ApplyInstructions(messages, node.Instructions)),
            AgentflowNodeKind.CheckpointMarker =>
                BindChatTransform(node.NodeId, messages => messages),
            AgentflowNodeKind.Input =>
                new InputPassthroughAgent(node.NodeId, node.Name).BindAsExecutor(AgentHostOptions),
            AgentflowNodeKind.HumanGate =>
                RequestPort.Create<List<ChatMessage>, List<ChatMessage>>(node.NodeId).BindAsExecutor(),
            AgentflowNodeKind.ConcurrentBlock =>
                CreateConcurrentBlockBinding(
                    agentflowId,
                    node,
                    orderedNodes,
                    nodeIdToAgent,
                    sessionScope,
                    executionTraceContext),
            AgentflowNodeKind.HandoffBlock or AgentflowNodeKind.GroupChatBlock or AgentflowNodeKind.MagenticBlock =>
                CreateBlockAgent(
                    agentflowId,
                    node,
                    orderedNodes,
                    nodeIdToAgent,
                    sessionScope,
                    executionTraceContext) is { } blockAgent
                    ? new NodeScopedAgent(blockAgent, node.NodeId, node.Name, node.Instructions, sessionScope)
                        .BindAsExecutor(AgentHostOptions)
                    : null,
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

    private static ExecutorBinding? CreateConcurrentBlockBinding(
        Guid agentflowId,
        AgentflowNode blockNode,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext)
    {
        var nodeMap = orderedNodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var config = ReadBlockConfig(blockNode);
        var participantIds = config.ParticipantNodeIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        if (participantIds.Count == 0)
        {
            return null;
        }

        var participants = new List<AIAgent>();
        foreach (var participantId in participantIds)
        {
            if (!nodeMap.TryGetValue(participantId, out var participantNode) ||
                !nodeIdToAgent.TryGetValue(participantId, out var participantAgent))
            {
                return null;
            }

            participants.Add(CreateBlockParticipantAgent(
                participantAgent,
                agentflowId,
                $"{blockNode.NodeId}.{participantNode.NodeId}",
                participantNode,
                sessionScope,
                executionTraceContext));
        }

        Func<List<ChatMessage>, CancellationToken, ValueTask<List<ChatMessage>>> runConcurrentAsync =
            async (messages, cancellationToken) =>
            {
                var input = ApplyInstructions(messages, blockNode.Instructions);
                var tasks = participants
                    .Select(agent => agent.RunAsync(input, cancellationToken: cancellationToken))
                    .ToArray();
                var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
                return responses.SelectMany(response => response.Messages).ToList();
            };

        return runConcurrentAsync.BindAsExecutor<List<ChatMessage>, List<ChatMessage>>(
            blockNode.NodeId,
            ChatExecutorOptions,
            threadsafe: true);
    }

    private static AIAgent? CreateBlockAgent(
        Guid agentflowId,
        AgentflowNode blockNode,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext)
    {
        var nodeMap = orderedNodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var config = ReadBlockConfig(blockNode);
        var participantIds = config.ParticipantNodeIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];
        if (participantIds.Count == 0)
        {
            return null;
        }

        var participants = new List<(string NodeId, AIAgent Agent)>();
        foreach (var participantId in participantIds)
        {
            if (!nodeMap.TryGetValue(participantId, out var participantNode) ||
                !nodeIdToAgent.TryGetValue(participantId, out var participantAgent))
            {
                return null;
            }

            participants.Add((participantId, CreateBlockParticipantAgent(
                participantAgent,
                agentflowId,
                $"{blockNode.NodeId}.{participantNode.NodeId}",
                participantNode,
                sessionScope,
                executionTraceContext)));
        }

        return blockNode.Kind switch
        {
            AgentflowNodeKind.HandoffBlock =>
                BuildHandoffBlock(blockNode, participants.Select(x => x.Agent).ToList(), config),
            AgentflowNodeKind.GroupChatBlock =>
                BuildGroupChatBlock(blockNode, participants.Select(x => x.Agent).ToList(), config),
            AgentflowNodeKind.MagenticBlock =>
                BuildMagenticBlock(
                    agentflowId,
                    blockNode,
                    participants,
                    config,
                    nodeMap,
                    nodeIdToAgent,
                    sessionScope,
                    executionTraceContext),
            _ => null,
        };
    }

    private static AIAgent BuildHandoffBlock(
        AgentflowNode blockNode,
        IReadOnlyList<AIAgent> participants,
        AgentflowBlockConfig config)
    {
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(participants[0])
            .AddParticipants(participants.Skip(1));
        if (!string.IsNullOrWhiteSpace(config.HandoffInstructions))
        {
            builder = builder.WithHandoffInstructions(config.HandoffInstructions);
        }

        if (config.EnableReturnToPrevious == true)
        {
            builder = builder.EnableReturnToPrevious();
        }

        if (config.Autonomous == true)
        {
            builder = builder.WithAutonomousMode(
                config.AutonomousTurnLimit,
                config.ContinuationPrompt,
                participants,
                null!,
                null!);
        }

        return CreateBlockWorkflowAgent(builder.Build(), blockNode);
    }

    private static AIAgent BuildGroupChatBlock(
        AgentflowNode blockNode,
        IReadOnlyList<AIAgent> participants,
        AgentflowBlockConfig config)
    {
        var maxRounds = Math.Max(1, config.MaxRounds ?? 10);
        var workflow = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents =>
            {
                var manager = new RoundRobinGroupChatManager(
                    agents,
                    (roundRobinManager, _, _) =>
                        new ValueTask<bool>(roundRobinManager.IterationCount >= maxRounds));
                manager.MaximumIterationCount = maxRounds;
                return manager;
            })
            .AddParticipants(participants)
            .Build();
        return CreateBlockWorkflowAgent(workflow, blockNode);
    }

    private static AIAgent? BuildMagenticBlock(
        Guid agentflowId,
        AgentflowNode blockNode,
        IReadOnlyList<(string NodeId, AIAgent Agent)> participants,
        AgentflowBlockConfig config,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext)
    {
        var managerNodeId = participants[0].NodeId;
        var manager = participants[0].Agent;
        var team = participants.Skip(1).Select(x => x.Agent).ToList();
        if (!string.IsNullOrWhiteSpace(config.ManagerNodeId))
        {
            if (!nodeMap.TryGetValue(config.ManagerNodeId, out var managerNode) ||
                !nodeIdToAgent.TryGetValue(config.ManagerNodeId, out var managerAgent))
            {
                return null;
            }

            manager = CreateBlockParticipantAgent(
                managerAgent,
                agentflowId,
                $"{blockNode.NodeId}.{managerNode.NodeId}.manager",
                managerNode,
                sessionScope,
                executionTraceContext);
            managerNodeId = config.ManagerNodeId;
            team = participants
                .Where(participant => !string.Equals(participant.NodeId, managerNodeId, StringComparison.Ordinal))
                .Select(participant => participant.Agent)
                .ToList();
        }

        var builder = AgentWorkflowBuilder.CreateMagenticBuilderWith(manager)
            .AddParticipants(team);
        if (config.MaxRounds.HasValue)
        {
            builder = builder.WithMaxRounds(config.MaxRounds);
        }

        if (config.MaxStalls.HasValue)
        {
            builder = builder.WithMaxStalls(config.MaxStalls.Value);
        }

        if (config.MaxResets.HasValue)
        {
            builder = builder.WithMaxResets(config.MaxResets);
        }

        if (config.RequirePlanSignoff.HasValue)
        {
            builder = builder.RequirePlanSignoff(config.RequirePlanSignoff.Value);
        }

        return CreateBlockWorkflowAgent(builder.Build(), blockNode);
    }

    private static AIAgent CreateBlockParticipantAgent(
        AIAgent participantAgent,
        Guid agentflowId,
        string runtimeNodeId,
        AgentflowNode participantNode,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext)
    {
        var shouldTrace = participantNode.Kind == AgentflowNodeKind.Agent;
        return new NodeScopedAgent(
            participantAgent,
            runtimeNodeId,
            participantNode.Name,
            participantNode.Instructions,
            sessionScope,
            shouldTrace ? executionTraceContext : null,
            shouldTrace ? agentflowId : null,
            shouldTrace ? participantNode.NodeId : null,
            shouldTrace ? participantNode.RelateId : null);
    }

    private static AIAgent CreateBlockWorkflowAgent(Workflow workflow, AgentflowNode blockNode)
    {
        return workflow.AsAIAgent(
            id: blockNode.NodeId,
            name: blockNode.Name ?? blockNode.NodeId,
            description: string.Empty,
            includeWorkflowOutputsInResponse: true);
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

    private static List<ChatMessage> ApplyInstructions(
        IReadOnlyList<ChatMessage> messages,
        string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return messages.ToList();
        }

        var result = new List<ChatMessage>
        {
            new(ChatRole.System, instructions)
            {
                AuthorName = "agw",
            },
        };
        result.AddRange(messages);
        return result;
    }

    private static HashSet<string> GetBlockParticipantNodeIds(IReadOnlyList<AgentflowNode> nodes)
    {
        var participantNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.Where(node => IsBlockNode(node.Kind)))
        {
            var config = ReadBlockConfig(node);
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

    private static AgentflowBlockConfig ReadBlockConfig(AgentflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            return new AgentflowBlockConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<AgentflowBlockConfig>(node.ConfigJson, JsonOptions) ??
                   new AgentflowBlockConfig();
        }
        catch (JsonException)
        {
            return new AgentflowBlockConfig();
        }
    }

    private sealed record AgentflowBlockConfig
    {
        public string[]? ParticipantNodeIds { get; init; }
        public string? ManagerNodeId { get; init; }
        public int? MaxRounds { get; init; }
        public int? MaxStalls { get; init; }
        public int? MaxResets { get; init; }
        public bool? RequirePlanSignoff { get; init; }
        public string? HandoffInstructions { get; init; }
        public bool? EnableReturnToPrevious { get; init; }
        public bool? Autonomous { get; init; }
        public int? AutonomousTurnLimit { get; init; }
        public string? ContinuationPrompt { get; init; }
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

    private sealed class NodeScopedAgent : DelegatingAIAgent
    {
        private readonly string _nodeId;
        private readonly string? _name;
        private readonly string? _instructions;
        private readonly AgentflowAgentSessionScope? _sessionScope;
        private readonly AgentflowExecutionTraceContext? _executionTraceContext;
        private readonly Guid? _agentflowId;
        private readonly string? _traceNodeId;
        private readonly Guid? _agentId;

        public NodeScopedAgent(
            AIAgent innerAgent,
            string nodeId,
            string? name,
            string? instructions,
            AgentflowAgentSessionScope? sessionScope,
            AgentflowExecutionTraceContext? executionTraceContext = null,
            Guid? agentflowId = null,
            string? traceNodeId = null,
            Guid? agentId = null) : base(innerAgent)
        {
            _nodeId = nodeId;
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

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var scopedSession = await PrepareSessionAsync(session, cancellationToken).ConfigureAwait(false);
            var input = ApplyInstructions(messages.ToList(), _instructions);
            using var activity = StartExecutionActivity(input);
            try
            {
                var response = await InnerAgent
                    .RunAsync(input, scopedSession, options, cancellationToken)
                    .ConfigureAwait(false);
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

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var scopedSession = await PrepareSessionAsync(session, cancellationToken).ConfigureAwait(false);
            var input = ApplyInstructions(messages.ToList(), _instructions);
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

        private async Task<AgentSession?> PrepareSessionAsync(
            AgentSession? session,
            CancellationToken cancellationToken)
        {
            AgentSession scopedSession =
                session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            _sessionScope?.Initialize(scopedSession);
            return scopedSession;
        }
    }
}
