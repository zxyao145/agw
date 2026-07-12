using System.Text.Json;
using System.Text.Json.Serialization;

using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;

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
        return Compile(agentflow, orderedNodes, edges, nodeIdToAgent, sessionScope: null);
    }

    internal Workflow? Compile(
        Agentflow agentflow,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyList<AgentflowEdge> edges,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope)
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

            var binding = CreateBinding(node, orderedNodes, nodeIdToAgent, sessionScope);
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
            .WithDescription(agentflow.Description ?? string.Empty);

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
        AgentflowNode node,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope)
    {
        return node.Kind switch
        {
            AgentflowNodeKind.Agent or AgentflowNodeKind.WorkflowAsAgent =>
                nodeIdToAgent.TryGetValue(node.NodeId, out var agent)
                    ? new NodeScopedAgent(agent, node.NodeId, node.Name, node.Instructions, sessionScope)
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
                CreateConcurrentBlockBinding(node, orderedNodes, nodeIdToAgent, sessionScope),
            AgentflowNodeKind.HandoffBlock or AgentflowNodeKind.GroupChatBlock or AgentflowNodeKind.MagenticBlock =>
                CreateBlockAgent(node, orderedNodes, nodeIdToAgent, sessionScope) is { } blockAgent
                    ? new NodeScopedAgent(blockAgent, node.NodeId, node.Name, node.Instructions, sessionScope)
                        .BindAsExecutor(AgentHostOptions)
                    : null,
            AgentflowNodeKind.Output =>
                BindChatTransform(node.NodeId, messages => messages),
            _ => null,
        };
    }

    private static ExecutorBinding? CreateConcurrentBlockBinding(
        AgentflowNode blockNode,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope)
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

            participants.Add(new NodeScopedAgent(
                participantAgent,
                $"{blockNode.NodeId}.{participantNode.NodeId}",
                participantNode.Name,
                participantNode.Instructions,
                sessionScope));
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
        AgentflowNode blockNode,
        IReadOnlyList<AgentflowNode> orderedNodes,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope)
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

            participants.Add((participantId, new NodeScopedAgent(
                participantAgent,
                $"{blockNode.NodeId}.{participantNode.NodeId}",
                participantNode.Name,
                participantNode.Instructions,
                sessionScope)));
        }

        return blockNode.Kind switch
        {
            AgentflowNodeKind.HandoffBlock =>
                BuildHandoffBlock(blockNode, participants.Select(x => x.Agent).ToList(), config),
            AgentflowNodeKind.GroupChatBlock =>
                BuildGroupChatBlock(blockNode, participants.Select(x => x.Agent).ToList(), config),
            AgentflowNodeKind.MagenticBlock =>
                BuildMagenticBlock(blockNode, participants, config, nodeMap, nodeIdToAgent, sessionScope),
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
        AgentflowNode blockNode,
        IReadOnlyList<(string NodeId, AIAgent Agent)> participants,
        AgentflowBlockConfig config,
        IReadOnlyDictionary<string, AgentflowNode> nodeMap,
        IReadOnlyDictionary<string, AIAgent> nodeIdToAgent,
        AgentflowAgentSessionScope? sessionScope)
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

            manager = new NodeScopedAgent(
                managerAgent,
                $"{blockNode.NodeId}.{managerNode.NodeId}.manager",
                managerNode.Name,
                managerNode.Instructions,
                sessionScope);
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

        foreach (var group in edges.Where(edge => edge.Kind == AgentflowEdgeKind.FanOut).GroupBy(edge => edge.SourceNodeId))
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

        foreach (var group in edges.Where(edge => edge.Kind == AgentflowEdgeKind.FanIn).GroupBy(edge => edge.TargetNodeId))
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
                messages.All(message => !string.Equals(message.AuthorName, config.Author, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(config.Role) &&
                messages.All(message => !string.Equals(message.Role.Value, config.Role, StringComparison.OrdinalIgnoreCase)))
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
        [JsonPropertyName("equals")]
        public string? EqualsText { get; init; }
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
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    private sealed class NodeScopedAgent(
        AIAgent innerAgent,
        string nodeId,
        string? name,
        string? instructions,
        AgentflowAgentSessionScope? sessionScope) : DelegatingAIAgent(innerAgent)
    {
        protected override string? IdCore => nodeId;

        public override string? Name => name ?? InnerAgent.Name ?? nodeId;

        public override string? Description => InnerAgent.Description;

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var scopedSession = await PrepareSessionAsync(session, cancellationToken).ConfigureAwait(false);
            return await InnerAgent
                .RunAsync(ApplyInstructions(messages.ToList(), instructions), scopedSession, options, cancellationToken)
                .ConfigureAwait(false);
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var scopedSession = await PrepareSessionAsync(session, cancellationToken).ConfigureAwait(false);
            await foreach (var update in InnerAgent
                               .RunStreamingAsync(ApplyInstructions(messages.ToList(), instructions), scopedSession, options, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private async Task<AgentSession?> PrepareSessionAsync(
            AgentSession? session,
            CancellationToken cancellationToken)
        {
            AgentSession scopedSession = session ?? await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            sessionScope?.Initialize(scopedSession);
            return scopedSession;
        }
    }
}
