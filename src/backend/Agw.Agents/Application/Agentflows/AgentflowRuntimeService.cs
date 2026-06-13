using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Application.AgentRun;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MsAgentWorkflowBuilder = Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder;

namespace Agw.Agents.Application.Agentflows;

public record AgentflowExecutionAgentResult(Guid AgentId, string AgentName, int Order, string Output);

public record AgentflowExecutionResult(string TaskId, IReadOnlyList<AgwMessage> Messages);

public class AgentflowRuntimeService : RuntimeServiceBase
{
    private readonly ILogger<AgentflowRuntimeService> _logger;
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<AgentflowNode> _agentflowNodeRepository;
    private readonly IRepository<AgentflowEdge> _agentflowEdgeRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AgentflowDomainService _agentflowDomainService;
    private readonly IAgentRuntimeService _agentRuntimeService;

    public AgentflowRuntimeService(
        ILogger<AgentflowRuntimeService> logger,
        IRepository<Agentflow> agentflowRepository,
        IRepository<AgentflowNode> agentflowNodeRepository,
        IRepository<AgentflowEdge> agentflowEdgeRepository,
        IRepository<Agent> agentRepository,
        IUnitOfWork unitOfWork,
        AgentflowDomainService agentflowDomainService,
        IAgentRuntimeService agentRuntimeService)
    {
        _logger = logger;
        _agentflowRepository = agentflowRepository;
        _agentflowNodeRepository = agentflowNodeRepository;
        _agentflowEdgeRepository = agentflowEdgeRepository;
        _agentRepository = agentRepository;
        _unitOfWork = unitOfWork;
        _agentflowDomainService = agentflowDomainService;
        _agentRuntimeService = agentRuntimeService;
    }

    public Task<IReadOnlyList<Agentflow>> ListAsync() => _agentflowRepository.ListAsync();

    public Task<Agentflow?> GetAsync(Guid id) => _agentflowRepository.GetByIdAsync(id);

    public Task<IReadOnlyList<AgentflowNode>> ListNodesAsync(Guid agentflowId) =>
        _agentflowNodeRepository.ListAsync(x => x.AgentflowId == agentflowId);

    public Task<IReadOnlyList<AgentflowEdge>> ListEdgesAsync(Guid agentflowId) =>
        _agentflowEdgeRepository.ListAsync(x => x.AgentflowId == agentflowId);

    public async Task<Agentflow?> CreateAsync(
        Agentflow agentflow,
        IReadOnlyList<AgentflowNode> nodes,
        IReadOnlyList<AgentflowEdge> edges,
        string user)
    {
        if (!_agentflowDomainService.TryPrepareForCreate(agentflow, user))
        {
            return null;
        }

        var existingAgentIds = await ListExistingAgentIdsAsync(nodes);
        var (normalizedNodes, normalizedEdges) = _agentflowDomainService.ValidateAndNormalizeGraph(
            agentflow.Pattern,
            nodes,
            edges,
            agentflow.Id,
            existingAgentIds);
        if (normalizedNodes == null || normalizedEdges == null)
        {
            return null;
        }

        await _agentflowRepository.AddAsync(agentflow);
        foreach (var node in normalizedNodes)
        {
            node.CreateBy = user;
            node.CreateTime = agentflow.CreateTime;
            await _agentflowNodeRepository.AddAsync(node);
        }

        foreach (var edge in normalizedEdges)
        {
            edge.CreateBy = user;
            edge.CreateTime = agentflow.CreateTime;
            await _agentflowEdgeRepository.AddAsync(edge);
        }

        await _unitOfWork.SaveChangesAsync();
        return agentflow;
    }

    public async Task<Agentflow?> UpdateAsync(
        Guid id,
        Action<Agentflow> updateAction,
        IReadOnlyList<AgentflowNode>? nodes,
        IReadOnlyList<AgentflowEdge>? edges,
        string user)
    {
        var existing = await _agentflowRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        if (!_agentflowDomainService.TryApplyUpdate(existing, updateAction, user))
        {
            return null;
        }

        if (nodes != null && edges != null)
        {
            var existingAgentIds = await ListExistingAgentIdsAsync(nodes);
            var (normalizedNodes, normalizedEdges) = _agentflowDomainService.ValidateAndNormalizeGraph(
                existing.Pattern,
                nodes,
                edges,
                existing.Id,
                existingAgentIds);
            if (normalizedNodes == null || normalizedEdges == null)
            {
                return null;
            }

            var currentNodes = await _agentflowNodeRepository.ListAsync(x => x.AgentflowId == existing.Id);
            foreach (var item in currentNodes)
            {
                _agentflowNodeRepository.Remove(item);
            }

            var currentEdges = await _agentflowEdgeRepository.ListAsync(x => x.AgentflowId == existing.Id);
            foreach (var item in currentEdges)
            {
                _agentflowEdgeRepository.Remove(item);
            }

            foreach (var node in normalizedNodes)
            {
                node.CreateBy ??= existing.CreateBy;
                node.CreateTime = existing.CreateTime == default ? DateTime.UtcNow : existing.CreateTime;
                node.UpdateBy = user;
                node.UpdateTime = DateTime.UtcNow;
                await _agentflowNodeRepository.AddAsync(node);
            }

            foreach (var edge in normalizedEdges)
            {
                edge.CreateBy ??= existing.CreateBy;
                edge.CreateTime = existing.CreateTime == default ? DateTime.UtcNow : existing.CreateTime;
                edge.UpdateBy = user;
                edge.UpdateTime = DateTime.UtcNow;
                await _agentflowEdgeRepository.AddAsync(edge);
            }
        }

        _agentflowRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _agentflowRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _agentflowRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default)
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

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            yield break;
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

        var run = await InProcessExecution.RunStreamingAsync(workflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (var evt in run.WatchStreamAsync().ConfigureAwait(false))
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

                case AgentResponseUpdateEvent updateEvt when updateEvt.Data is AgentResponseUpdate update:
                    _logger.LogInformation("AgentResponseUpdateEvent {ExecutorId}, {Data}", updateEvt.ExecutorId,
                        updateEvt.Data);
                    var chatMsg = update.ToAiMessage();
                    if (chatMsg != null)
                    {
                        yield return chatMsg;
                    }

                    break;

                case WorkflowOutputEvent outputEvt:
                    _logger.LogInformation("Workflow output: {Data}", outputEvt.Data);
                    // List<ChatMessage> result = outputEvt.As<List<ChatMessage>>()!;
                    //
                    // foreach (ChatMessage chatMessage in result)
                    // {
                    //     var agwMsg = chatMessage.ToAiMessage();
                    //     if (agwMsg != null)
                    //     {
                    //         yield return agwMsg;
                    //     }
                    // }
                    //
                    break;

                case WorkflowErrorEvent error:
                    _logger.LogError(error.Exception, "Workflow error");
                    break;
            }
        }

        yield return CreateTurnFinishedMessage(cancellationToken);
    }

    public async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        string input,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, input)
            {
                AuthorName = Constants.DefaultAuthor
            }
        };

        return await ExecuteAsync(agentflowId, taskId, messages, cancellationToken, projectId, contextId)
            .ConfigureAwait(false);
    }

    public async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid taskId,
        List<ChatMessage> messages,
        CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null)
    {
        return await ExecuteAsync(agentflowId, ProjectDefaults.GetDefaultProjectIdentifier(projectId), taskId, messages,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Workflow?> CreateAiWorkflow(Guid agentflowId, CancellationToken cancellationToken = default)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            return null;
        }

        return await CreateAiWorkflow(agentflow, cancellationToken);
    }

    private async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid projectId,
        Guid? taskId,
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            return null;
        }

        if (taskId == null)
        {
            taskId = Guid.NewGuid();
        }

        var workflow = await CreateAiWorkflow(agentflow, cancellationToken);
        if (workflow == null)
        {
            return null;
        }

        var run = await InProcessExecution.RunStreamingAsync(workflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var result = new List<ChatMessage>();
        await foreach (var evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            if (evt is AgentResponseUpdateEvent updateEvt)
            {
                _logger.LogDebug("{ExecutorId}: {Data}", updateEvt.ExecutorId, updateEvt.Data);
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                result = (List<ChatMessage>)outputEvt.Data!;
                break;
            }
        }

        var outputs = new List<AgwMessage>();
        foreach (var message in result)
        {
            var contentObj = new AgwTextContent { Content = message.Text };
            var chatMsg = new AgwMessage(message.MessageId ?? string.Empty, message.AuthorName, message.Role,
                [contentObj]);
            outputs.Add(chatMsg);
        }

        return new AgentflowExecutionResult(taskId.Value.Normalize(), outputs);
    }

    private async Task<Workflow?> CreateAiWorkflow(Agentflow agentflow, CancellationToken cancellationToken)
    {
        var agentflowNodes = await _agentflowNodeRepository.ListAsync(x => x.AgentflowId == agentflow.Id);
        var agentflowEdges = await _agentflowEdgeRepository.ListAsync(x => x.AgentflowId == agentflow.Id);
        if (agentflowNodes.Count == 0)
        {
            return null;
        }

        var config = ParseConfiguration(agentflow.ConfigurationJson);
        var orderedNodes = _agentflowDomainService.OrderNodesByEdges(agentflowNodes, agentflowEdges, agentflow.Pattern);
        var nodeIdToAgent = new Dictionary<string, AIAgent>(StringComparer.Ordinal);
        var aiAgents = new List<AIAgent>();

        foreach (var node in orderedNodes)
        {
            AIAgent? aiAgent;
            if (node.Type == AgentflowNodeType.AgentNode)
            {
                aiAgent = await _agentRuntimeService.CreateAiAgentAsync(node.RelateId,
                    cancellationToken: cancellationToken);
            }
            else
            {
                var flowNode = await CreateAiWorkflow(node.RelateId, cancellationToken);
                aiAgent = flowNode?.AsAIAgent();
            }

            if (aiAgent == null)
            {
                return null;
            }

            aiAgents.Add(aiAgent);
            nodeIdToAgent[node.NodeId] = aiAgent;
        }

        Workflow? workflow = null;
        switch (agentflow.Pattern)
        {
            case AgentflowOrchestrationPattern.Concurrent:
            {
                workflow = MsAgentWorkflowBuilder.BuildConcurrent(aiAgents);
                break;
            }
            case AgentflowOrchestrationPattern.Sequential:
            {
                var options = new AIAgentHostOptions
                {
                    ForwardIncomingMessages = false,
                    ReassignOtherAgentsAsUsers = false
                };
                var executorBindings = aiAgents
                        .Select(x => x.BindAsExecutor(options))
                        .ToList()
                    ;

                if (executorBindings.Count == 0)
                {
                    throw new ArgumentException("Executors cannot be empty.", nameof(executorBindings));
                }

                var builder = new WorkflowBuilder(executorBindings[0]);
                for (var i = 0; i < executorBindings.Count - 1; i++)
                {
                    builder.AddEdge(executorBindings[i], executorBindings[i + 1]);
                }

                workflow = builder.Build();

                // workflow = MsAgentWorkflowBuilder.BuildConcurrent(aiAgents);
                break;
                
            }
            case AgentflowOrchestrationPattern.GroupChat:
            {
                workflow = MsAgentWorkflowBuilder.CreateGroupChatBuilderWith(agents =>
                        new RoundRobinGroupChatManager(agents)
                        {
                            MaximumIterationCount = GetConfigInt(config, "maximumIterationCount", 5)
                        })
                    .AddParticipants(aiAgents.ToArray())
                    .Build();
                break;
            }
            case AgentflowOrchestrationPattern.Handoff:
            {
                workflow = DxAgentWorkflowBuilder.BuildHandoff(aiAgents, agentflowEdges,
                    nodeIdToAgent);
                break;
            }
            case AgentflowOrchestrationPattern.Magentic:
                throw new AgwException(ErrorCodes.MagenticNotSupported,
                    "Magentic not supported now");
            default:
                workflow = null;
                break;
        }

        ;

        return workflow;
    }

    private async Task<IReadOnlyCollection<Guid>> ListExistingAgentIdsAsync(IReadOnlyList<AgentflowNode> nodes)
    {
        var agentIds = nodes
            .Where(x => x.Type == AgentflowNodeType.AgentNode)
            .Select(x => x.RelateId)
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        if (agentIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var existingAgents = await _agentRepository.ListAsync(x => agentIds.Contains(x.Id));
        return existingAgents.Select(x => x.Id).ToList();
    }

    private static Dictionary<string, JsonElement> ParseConfiguration(string? configurationJson)
    {
        var config = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
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
        }

        return config;
    }

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
}
