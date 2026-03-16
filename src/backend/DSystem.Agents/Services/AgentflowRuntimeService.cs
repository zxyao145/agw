using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using DSystem.Domain.Services;
using DSystem.Shared;
using DSystem.Shared.Enums;
using DSystem.Shared.Models;
using DSystem.Shared.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MsAgentWorkflowBuilder = Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder;

namespace DSystem.Appliaction.Services;

public interface IAgentflowAgentExecutor
{
    Task<string> ExecuteAsync(AiAgent agent, string input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Placeholder executor so workflow orchestration can run end-to-end without a real LLM/Agent Framework integration yet.
/// </summary>
public class PlaceholderAgentflowAgentExecutor : IAgentflowAgentExecutor
{
    public Task<string> ExecuteAsync(AiAgent agent, string input, CancellationToken cancellationToken = default)
    {
        var output = $"[placeholder] agent={agent.Name}; input={input}";
        return Task.FromResult(output);
    }
}

public record AgentflowExecutionAgentResult(Guid AgentId, string AgentName, int Order, string Output);

public record AgentflowExecutionResult(string SessionId, IReadOnlyList<AiMessage> Messages);

public class AgentflowRuntimeService
{
    private readonly ILogger<AgentflowRuntimeService> _logger;
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<AgentflowNode> _agentflowNodeRepository;
    private readonly IRepository<AgentflowEdge> _agentflowEdgeRepository;
    private readonly AgentRuntimeService _agentRuntimeService;
    private readonly IAgentflowAgentExecutor _executor;

    public AgentflowRuntimeService(
        ILogger<AgentflowRuntimeService> logger,
        IRepository<Agentflow> agentflowRepository,
        IRepository<AgentflowNode> agentflowAgentRepository,
        IRepository<AgentflowEdge> agentflowEdgeRepository,
        AgentRuntimeService agentRuntimeService,
        IAgentflowAgentExecutor executor)
    {
        _logger = logger;
        _agentflowRepository = agentflowRepository;
        _agentflowNodeRepository = agentflowAgentRepository;
        _agentRuntimeService = agentRuntimeService;
        _executor = executor;
        _agentflowEdgeRepository = agentflowEdgeRepository;
    }

    public async Task<string?> GetMermaidAsync(
        Guid agentflowId,
        CancellationToken cancellationToken = default)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            return null;
        }

        var workflow = await CreateAiWorkflow(agentflow, cancellationToken);
        if (workflow == null)
        {
            return null;
        }

        var mermaidString = workflow.ToMermaidString();
        _logger.LogInformation("Constructed workflow: {Workflow}", mermaidString);
        return mermaidString;
    }



    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        string sessionId,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        string? projectId = null,
        string? contextId = null)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            yield break;
        }
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().Normalize();
        }

        var workflow = await CreateAiWorkflow(agentflow, cancellationToken);
        if (workflow == null)
        {
            yield break;
        }
        var mermaidString = workflow.ToMermaidString();
        _logger.LogInformation("Constructed workflow: {Workflow}", mermaidString);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, input)
        };

        StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var responseUpdates = new List<AgentResponseUpdate>();

        await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            _logger.LogInformation("WorkflowEvent Type {Type}", evt.GetType().Name);
            switch (evt)
            {
                case ExecutorInvokedEvent invoke:
                    _logger.LogInformation("Starting {ExecutorId}", invoke.ExecutorId);
                    break;

                case ExecutorCompletedEvent complete:
                    _logger.LogInformation("Completed {ExecutorId}, {Data}", complete.ExecutorId, complete.Data);
                    break;

                case AgentResponseUpdateEvent updateEvt:
                    {
                        _logger.LogInformation("AgentResponseUpdateEvent {ExecutorId}, {Data}", updateEvt.ExecutorId, updateEvt.Data);
                        if (updateEvt.Data is AgentResponseUpdate update)
                        {
                            responseUpdates.Add(update);
                            var chatMsg = update.ToAiMessage();
                            if (chatMsg != null)
                            {
                                yield return chatMsg;
                            }
                        }
                    }
                    break;

                case WorkflowOutputEvent output:
                    {
                        _logger.LogInformation("Workflow output: {Data}", output.Data);
                        //if(output.Data is List<Microsoft.Extensions.AI.ChatMessage> outputMessages)
                        //{
                        //    foreach (var item in outputMessages)
                        //    {
                        //        var chatMsg = item.ToAiMessage();
                        //        if (chatMsg != null)
                        //        {
                        //            yield return chatMsg;
                        //        }
                        //    }
                        //}
                    }
                    break;

                case WorkflowErrorEvent error:
                    _logger.LogError(error.Exception, "Workflow error");
                    break;
            }
        }

        //await _taskRecordApplication.SaveThreadStateAsync(
        //    sessionId,
        //    string.IsNullOrWhiteSpace(contextId) ? sessionId : contextId,
        //    projectId ?? string.Empty,
        //    ProjectTaskAgentType.Agentflow,
        //    agentflowId,
        //    agentflow.Name,
        //    responseUpdates,
        //    input,
        //    cancellationToken: CancellationToken.None);
    }

    public async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        string sessionId,
        string input,
        CancellationToken cancellationToken = default,
        string? projectId = null,
        string? contextId = null)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = Guid.NewGuid().Normalize();
        }

        var workflow = await CreateAiWorkflow(agentflow, cancellationToken);
        if (workflow == null)
        {
            return null;
        }


        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, input)
        };

        StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        List<ChatMessage> result = new();
        await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            if (evt is AgentResponseUpdateEvent e)
            {
                _logger.LogDebug($"{e.ExecutorId}: {e.Data}");
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                result = (List<ChatMessage>)outputEvt.Data!;
                break;
            }
        }

        var outputs = new List<AiMessage>();
        var responseUpdates = new List<AgentResponseUpdate>();

        // Display aggregated results from all agents
        _logger.LogInformation("===== Final Aggregated Results =====");
        foreach (var message in result)
        {
            responseUpdates.Add(ToResponseUpdate(message));
            var contentObj = new AiMessageContent("text", message.Text);
            var chatMsg =
                new AiMessage(message.MessageId ?? "", message.AuthorName, message.Role.Value, [contentObj]);
            outputs.Add(chatMsg);
        }

        //await _taskRecordApplication.SaveThreadStateAsync(
        //    sessionId,
        //    string.IsNullOrWhiteSpace(contextId) ? sessionId : contextId,
        //    projectId ?? string.Empty,
        //    ProjectTaskAgentType.Agentflow,
        //    agentflowId,
        //    agentflow.Name,
        //    responseUpdates,
        //    input,
        //    cancellationToken: CancellationToken.None);

        return new AgentflowExecutionResult(sessionId, Messages: outputs);
    }


    public async Task<Workflow?>
        CreateAiWorkflow(Guid agentflowId, CancellationToken cancellationToken)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            return null;
        }
        return await CreateAiWorkflow(agentflow, cancellationToken);
    }

    private async Task<Workflow?>
        CreateAiWorkflow(Agentflow agentflow, CancellationToken cancellationToken)
    {
        Guid agentflowId = agentflow.Id;

        var agentflowNodes = await _agentflowNodeRepository
            .ListAsync(x => x.AgentflowId == agentflowId);

        var agentflowEdges = await _agentflowEdgeRepository
            .ListAsync(x => x.AgentflowId == agentflowId);

        if (agentflowNodes.Count == 0)
        {
            return null;
        }

        // Parse configuration JSON for pattern-specific settings
        var config = ParseConfiguration(agentflow.ConfigurationJson);

        // Order nodes based on edges for patterns that require ordering
        var orderedNodes = OrderNodesByEdges(agentflowNodes, agentflowEdges, agentflow.Pattern);

        // Create a map from NodeId to AIAgent for handoff routing
        var nodeIdToAgent = new Dictionary<string, AIAgent>();
        List<AIAgent> aiAgents = new();

        foreach (var node in orderedNodes)
        {
            AIAgent? aiAgent;
            if (node.Type == AgentflowNodeType.AgentNode)
            {
                aiAgent = await _agentRuntimeService.CreateAiAgentAsync(node.RelateId);
            }
            else
            {
                var flowNode = await this.CreateAiWorkflow(node.RelateId, cancellationToken);
                aiAgent = flowNode?.AsAIAgent() ?? null;
            }

            if (aiAgent == null)
            {
                return null;
            }

            aiAgents.Add(aiAgent);
            nodeIdToAgent[node.NodeId] = aiAgent;
        }

        Workflow? aiFlow;
        switch (agentflow.Pattern)
        {
            case AgentflowOrchestrationPattern.Concurrent:
                aiFlow = MsAgentWorkflowBuilder.BuildConcurrent(aiAgents);
                break;

            case AgentflowOrchestrationPattern.Sequential:
                aiFlow = MsAgentWorkflowBuilder.BuildSequential(aiAgents);
                break;

            case AgentflowOrchestrationPattern.GroupChat:
                var maxIterations = GetConfigInt(config, "maximumIterationCount", 5);
                aiFlow = MsAgentWorkflowBuilder.CreateGroupChatBuilderWith(
                     agents => new RoundRobinGroupChatManager(agents)
                     {
                         MaximumIterationCount = maxIterations
                     })
                     .AddParticipants(aiAgents.ToArray())
                     .Build();
                break;

            case AgentflowOrchestrationPattern.Handoff:
                aiFlow = DxAgentWorkflowBuilder.BuildHandoff(aiAgents, agentflowEdges, nodeIdToAgent);
                break;

            case AgentflowOrchestrationPattern.Magentic:
                throw new NotSupportedException("Magentic not supported now");
            //aiFlow = DxAgentWorkflowBuilder.BuildMagentic(
            //    aiAgents,
            //    maxRounds: GetConfigInt(config, "maxRounds", 10),
            //    maxStallCount: GetConfigInt(config, "maxStallCount", 3),
            //    maxResetCount: GetConfigInt(config, "maxResetCount", 2));
            //break;

            default:
                aiFlow = null;
                break;
        }

        return aiFlow;
    }

    /// <summary>
    /// Parses the configuration JSON and returns a dictionary of settings.
    /// </summary>
    private static Dictionary<string, JsonElement> ParseConfiguration(string? configurationJson)
    {
        var config = new Dictionary<string, JsonElement>();
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            return config;
        }

        try
        {
            using var doc = JsonDocument.Parse(configurationJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                config[prop.Name] = prop.Value.Clone();
            }
        }
        catch (JsonException)
        {
            // Ignore invalid JSON
        }

        return config;
    }

    /// <summary>
    /// Gets an integer value from the configuration dictionary with a default fallback.
    /// </summary>
    private static int GetConfigInt(Dictionary<string, JsonElement> config, string key, int defaultValue)
    {
        if (config.TryGetValue(key, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var value))
        {
            return value;
        }
        return defaultValue;
    }

    /// <summary>
    /// Orders nodes based on edges using topological sort for sequential patterns,
    /// or returns nodes in their original order for other patterns.
    /// </summary>
    private static IReadOnlyList<AgentflowNode> OrderNodesByEdges(
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        AgentflowOrchestrationPattern pattern)
    {
        // For patterns that don't require ordering, return as-is
        if (pattern == AgentflowOrchestrationPattern.Concurrent ||
            pattern == AgentflowOrchestrationPattern.GroupChat)
        {
            return nodes;
        }

        // If no edges, return nodes as-is
        if (edges.Count == 0)
        {
            return nodes;
        }

        // Build data structures for Kahn's topological sort algorithm
        var nodeMap = nodes.ToDictionary(n => n.NodeId);

        // Adjacency List (adjList):
        // A graph representation where each node maps to a list of its outgoing neighbors.
        // For example, if we have edges A→B and A→C, then adjList["A"] = ["B", "C"].
        // This represents "which nodes can this node reach directly?"
        // Used to traverse the graph and update in-degrees when a node is processed.
        var adjList = new Dictionary<string, List<string>>();

        // In-Degree Map (inDegree):
        // Tracks the number of incoming edges for each node.
        // A node with inDegree=0 has no dependencies and can be processed immediately.
        // For example, in A→B→C: inDegree["A"]=0, inDegree["B"]=1, inDegree["C"]=1.
        // Nodes are processed in order of their in-degrees reaching zero.
        var inDegree = new Dictionary<string, int>();

        foreach (var node in nodes)
        {
            adjList[node.NodeId] = new List<string>();
            inDegree[node.NodeId] = 0;
        }

        foreach (var edge in edges)
        {
            if (adjList.ContainsKey(edge.SourceNodeId) && inDegree.ContainsKey(edge.TargetNodeId))
            {
                adjList[edge.SourceNodeId].Add(edge.TargetNodeId);
                inDegree[edge.TargetNodeId]++;
            }
        }

        // Kahn's algorithm for topological sort
        var queue = new Queue<string>();
        foreach (var node in nodes)
        {
            if (inDegree[node.NodeId] == 0)
            {
                queue.Enqueue(node.NodeId);
            }
        }

        var sorted = new List<AgentflowNode>();
        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            sorted.Add(nodeMap[nodeId]);

            foreach (var neighbor in adjList[nodeId])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                {
                    queue.Enqueue(neighbor);
                }
            }
        }

        // If we couldn't sort all nodes (cycle detected), return original order
        if (sorted.Count != nodes.Count)
        {
            return nodes;
        }

        return sorted;
    }

    private static AgentResponseUpdate ToResponseUpdate(ChatMessage message) =>
        new()
        {
            MessageId = message.MessageId ?? Guid.NewGuid().Normalize(),
            AuthorName = message.AuthorName,
            Role = message.Role,
            Contents = [new TextContent(message.Text ?? string.Empty)]
        };
}

