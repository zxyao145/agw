using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Runtime.AgentRun;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Extensions;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Runtime.Agentflows;

public record AgentflowExecutionResult(
    string TaskId,
    string ContextId,
    IReadOnlyList<AgwMessage> Messages)
{
    public AgentflowExecutionResult(
        string taskId,
        IReadOnlyList<AgwMessage> messages)
        : this(taskId, taskId, messages)
    {
    }
}

public class AgentflowRuntimeService : RuntimeServiceBase, IAgentflowRuntimeService
{
    private const string DefaultHumanGateMode = "approval";
    private const string DefaultHumanGatePrompt = "Human approval is required to continue.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<AgentflowRuntimeService> _logger;
    private readonly IRepository<Agentflow> _agentflowRepository;
    private readonly IRepository<AgentflowNode> _agentflowNodeRepository;
    private readonly IRepository<AgentflowEdge> _agentflowEdgeRepository;
    private readonly AgentflowDomainService _agentflowDomainService;
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly IProviderSessionState _providerSessionState;
    private readonly AgentflowWorkflowCompiler _workflowCompiler = new();

    public AgentflowRuntimeService(
        ILogger<AgentflowRuntimeService> logger,
        IRepository<Agentflow> agentflowRepository,
        IRepository<AgentflowNode> agentflowNodeRepository,
        IRepository<AgentflowEdge> agentflowEdgeRepository,
        AgentflowDomainService agentflowDomainService,
        IAgentRuntimeService agentRuntimeService,
        IProviderSessionState providerSessionState)
    {
        _logger = logger;
        _agentflowRepository = agentflowRepository;
        _agentflowNodeRepository = agentflowNodeRepository;
        _agentflowEdgeRepository = agentflowEdgeRepository;
        _agentflowDomainService = agentflowDomainService;
        _agentRuntimeService = agentRuntimeService;
        _providerSessionState = providerSessionState;
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

        var mermaidString = WorkflowVisualizer.ToMermaidString(workflow);
        _logger.LogInformation("Constructed workflow: {Workflow}", mermaidString);
        return mermaidString;
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        Guid agentflowId,
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Guid? projectId = null,
        string? contextId = null,
        Guid? taskId = null,
        IHumanGateApprovalHandler? humanGateApprovalHandler = null)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            yield break;
        }

        var resolvedProjectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        var sessionScope = CreateSessionScope(resolvedProjectId, contextId, taskId);
        var workflow = await CreateAiWorkflow(agentflow, cancellationToken, sessionScope);
        if (workflow == null)
        {
            yield break;
        }

        var humanGateNodes = (await _agentflowNodeRepository.ListAsync(
                x => x.AgentflowId == agentflow.Id && x.Kind == AgentflowNodeKind.HumanGate))
            .ToDictionary(node => node.NodeId, StringComparer.Ordinal);

        var mermaidString = WorkflowVisualizer.ToMermaidString(workflow);
        _logger.LogInformation("Constructed workflow: {Workflow}", mermaidString);

        var messages = CreateWorkflowInputMessages(input);

        var run = await InProcessExecution.RunStreamingAsync(workflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        var executorsWithUpdates = new HashSet<string>(StringComparer.Ordinal);

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

                case RequestInfoEvent requestInfo:
                    var externalRequest = requestInfo.Request;
                    _logger.LogInformation(
                        "External request {RequestId} from port {PortId}",
                        externalRequest.RequestId,
                        externalRequest.PortInfo.PortId);

                    if (!humanGateNodes.TryGetValue(externalRequest.PortInfo.PortId, out var humanGateNode))
                    {
                        break;
                    }

                    if (humanGateApprovalHandler == null)
                    {
                        _logger.LogWarning(
                            "HumanGate {PortId} requested approval but no approval handler was provided.",
                            externalRequest.PortInfo.PortId);
                        await run.CancelRunAsync();
                        yield return CreateHumanGateUnavailableMessage(humanGateNode);
                        yield return CreateTurnFinishedMessage(cancellationToken);
                        yield break;
                    }

                    var approvalRequest = CreateHumanGateApprovalRequest(externalRequest, humanGateNode);
                    var approvalTask = humanGateApprovalHandler
                        .WaitForApprovalAsync(approvalRequest, cancellationToken)
                        .AsTask();

                    yield return CreateHumanGateApprovalRequestMessage(approvalRequest);

                    HumanGateApprovalDecision decision;
                    try
                    {
                        decision = await approvalTask;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        await run.CancelRunAsync();
                        yield break;
                    }

                    if (!decision.Approved)
                    {
                        await run.CancelRunAsync();
                        yield return CreateHumanGateRejectedMessage(approvalRequest);
                        yield return CreateTurnFinishedMessage(cancellationToken);
                        yield break;
                    }

                    var responseMessages = CreateHumanGateResponseMessages(
                        approvalRequest.Messages,
                        decision);
                    await run.SendResponseAsync(externalRequest.CreateResponse(responseMessages));
                    break;

                case AgentResponseUpdateEvent updateEvt when updateEvt.Data is AgentResponseUpdate update:
                    _logger.LogInformation("AgentResponseUpdateEvent {ExecutorId}, {Data}", updateEvt.ExecutorId,
                        updateEvt.Data);
                    executorsWithUpdates.Add(updateEvt.ExecutorId);
                    var chatMsg = update.ToAiMessage();
                    if (chatMsg != null)
                    {
                        yield return chatMsg;
                    }

                    break;

                case AgentResponseEvent responseEvt when responseEvt.Data is AgentResponse response:
                    _logger.LogInformation("AgentResponseEvent {ExecutorId}, {Data}", responseEvt.ExecutorId,
                        responseEvt.Data);
                    if (executorsWithUpdates.Contains(responseEvt.ExecutorId))
                    {
                        break;
                    }

                    foreach (var responseMsg in response.Messages.Select(message => message.ToAiMessage()).OfType<AgwMessage>())
                    {
                        yield return responseMsg;
                    }

                    break;

                case WorkflowOutputEvent outputEvt:
                    _logger.LogInformation("Workflow output: {Data}", outputEvt.Data);
                    foreach (var outputMessage in CreateWorkflowOutputMessages(outputEvt.Data))
                    {
                        yield return outputMessage;
                    }

                    break;

                case WorkflowErrorEvent error:
                    _logger.LogError(error.Exception, "Workflow error");
                    yield return CreateWorkflowErrorMessage(error.Exception);
                    yield return CreateTurnFinishedMessage(cancellationToken);
                    yield break;
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
                AuthorName = Constants.DefaultInputAuthor
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
        return await ExecuteAsync(
            agentflowId,
            ProjectDefaults.GetDefaultProjectIdentifier(projectId),
            taskId,
            messages,
            cancellationToken,
            contextId).ConfigureAwait(false);
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

    private AgentflowAgentSessionScope? CreateSessionScope(
        Guid projectId,
        string? contextId,
        Guid? taskId)
    {
        if (string.IsNullOrWhiteSpace(contextId))
        {
            return null;
        }

        return new AgentflowAgentSessionScope(
            _providerSessionState,
            projectId,
            contextId.Trim(),
            taskId);
    }

    private async Task<Workflow?> CreateAiWorkflow(
        Guid agentflowId,
        CancellationToken cancellationToken,
        AgentflowAgentSessionScope? sessionScope)
    {
        var agentflow = await _agentflowRepository.GetByIdAsync(agentflowId);
        if (agentflow == null || !agentflow.Enable)
        {
            return null;
        }

        return await CreateAiWorkflow(agentflow, cancellationToken, sessionScope);
    }

    private async Task<AgentflowExecutionResult?> ExecuteAsync(
        Guid agentflowId,
        Guid projectId,
        Guid? taskId,
        List<ChatMessage> messages,
        CancellationToken cancellationToken,
        string? contextId = null)
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

        var sessionScope = CreateSessionScope(projectId, contextId, taskId);
        var workflow = await CreateAiWorkflow(agentflow, cancellationToken, sessionScope);
        if (workflow == null)
        {
            return null;
        }

        var run = await InProcessExecution.RunStreamingAsync(workflow, messages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        var outputs = new List<AgwMessage>();
        await foreach (var evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            if (evt is AgentResponseUpdateEvent updateEvt)
            {
                _logger.LogDebug("{ExecutorId}: {Data}", updateEvt.ExecutorId, updateEvt.Data);
            }
            else if (evt is WorkflowOutputEvent outputEvt)
            {
                outputs.AddRange(CreateWorkflowOutputMessages(outputEvt.Data));
                break;
            }
        }

        var taskIdString = taskId.Value.Normalize();
        var resolvedContextId = ExecutionContextIdResolver.Resolve(contextId);

        return new AgentflowExecutionResult(taskIdString, resolvedContextId, outputs);
    }

    internal static IReadOnlyList<AgwMessage> CreateWorkflowOutputMessages(object? data)
    {
        return data switch
        {
            null => [],
            ChatMessage message => ConvertChatMessages([message]),
            IEnumerable<ChatMessage> messages => ConvertChatMessages(messages),
            AgentResponse response => ConvertChatMessages(response.Messages),
            IEnumerable<AgentResponse> responses => responses
                .SelectMany(response => ConvertChatMessages(response.Messages))
                .ToList(),
            AgentResponseUpdate update => update.ToAiMessage() is { } message ? [message] : [],
            IEnumerable<AgentResponseUpdate> updates => updates
                .Select(update => update.ToAiMessage())
                .OfType<AgwMessage>()
                .ToList(),
            _ => [],
        };
    }

    internal static List<ChatMessage> CreateWorkflowInputMessages(string input) =>
    [
        new(ChatRole.User, input)
        {
            AuthorName = Constants.DefaultInputAuthor
        }
    ];

    private static IReadOnlyList<AgwMessage> ConvertChatMessages(IEnumerable<ChatMessage> messages)
    {
        return messages
            .Select(message => message.ToAiMessage())
            .OfType<AgwMessage>()
            .ToList();
    }

    private async Task<Workflow?> CreateAiWorkflow(
        Agentflow agentflow,
        CancellationToken cancellationToken,
        AgentflowAgentSessionScope? sessionScope = null)
    {
        var agentflowNodes = await _agentflowNodeRepository.ListAsync(x => x.AgentflowId == agentflow.Id);
        var agentflowEdges = await _agentflowEdgeRepository.ListAsync(x => x.AgentflowId == agentflow.Id);
        if (agentflowNodes.Count == 0)
        {
            return null;
        }

        var orderedNodes = _agentflowDomainService.OrderNodesByEdges(agentflowNodes, agentflowEdges);
        var nodeIdToAgent = new Dictionary<string, AIAgent>(StringComparer.Ordinal);

        foreach (var node in orderedNodes)
        {
            AIAgent? aiAgent;
            if (node.Kind == AgentflowNodeKind.Agent && node.RelateId.HasValue)
            {
                aiAgent = await _agentRuntimeService.CreateAiAgentAsync(
                    node.RelateId.Value,
                    sessionScope?.ProjectId,
                    sessionScope?.TaskId,
                    resume: false,
                    cancellationToken: cancellationToken);
            }
            else if (node.Kind == AgentflowNodeKind.WorkflowAsAgent && node.RelateId.HasValue)
            {
                var flowNode = await CreateAiWorkflow(node.RelateId.Value, cancellationToken, sessionScope);
                aiAgent = flowNode?.AsAIAgent();
            }
            else
            {
                continue;
            }

            if (aiAgent == null)
            {
                return null;
            }

            nodeIdToAgent[node.NodeId] = aiAgent;
        }

        if (nodeIdToAgent.Count == 0)
        {
            return null;
        }

        return _workflowCompiler.Compile(agentflow, orderedNodes, agentflowEdges, nodeIdToAgent, sessionScope);
    }

    private static HumanGateApprovalRequest CreateHumanGateApprovalRequest(
        ExternalRequest externalRequest,
        AgentflowNode node)
    {
        var config = ReadHumanGateConfig(node);
        var messages = externalRequest.TryGetDataAs<List<ChatMessage>>(out var requestedMessages) &&
            requestedMessages != null
                ? requestedMessages
                : [];

        var mode = string.IsNullOrWhiteSpace(config.HumanMode)
            ? DefaultHumanGateMode
            : config.HumanMode.Trim();
        var prompt = string.IsNullOrWhiteSpace(config.HumanPrompt)
            ? DefaultHumanGatePrompt
            : config.HumanPrompt.Trim();

        return new HumanGateApprovalRequest(
            externalRequest.RequestId,
            node.NodeId,
            node.Name,
            mode,
            prompt,
            messages);
    }

    private static List<ChatMessage> CreateHumanGateResponseMessages(
        IReadOnlyList<ChatMessage> messages,
        HumanGateApprovalDecision decision)
    {
        var responseMessages = messages.ToList();
        if (!string.IsNullOrWhiteSpace(decision.ResponseText))
        {
            responseMessages.Add(new ChatMessage(ChatRole.User, decision.ResponseText.Trim())
            {
                AuthorName = "human",
            });
        }

        return responseMessages;
    }

    private static AgwMessage CreateHumanGateApprovalRequestMessage(HumanGateApprovalRequest request)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-request" },
            { "requestId", request.RequestId },
            { "nodeId", request.NodeId },
            { "mode", request.Mode },
            { "prompt", request.Prompt },
        };

        if (!string.IsNullOrWhiteSpace(request.NodeName))
        {
            additionalProperties["nodeName"] = request.NodeName;
        }

        var latestMessageText = request.Messages.LastOrDefault()?.Text;
        if (!string.IsNullOrWhiteSpace(latestMessageText))
        {
            additionalProperties["inputPreview"] = latestMessageText;
        }

        return new AgwMessage(
            Guid.NewGuid().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = request.Prompt }],
            additionalProperties);
    }

    private static AgwMessage CreateHumanGateRejectedMessage(HumanGateApprovalRequest request)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-rejected" },
            { "requestId", request.RequestId },
            { "nodeId", request.NodeId },
        };

        return new AgwMessage(
            Guid.NewGuid().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = "HumanGate rejected. Workflow stopped." }],
            additionalProperties);
    }

    private static AgwMessage CreateHumanGateUnavailableMessage(AgentflowNode node)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-unavailable" },
            { "nodeId", node.NodeId },
        };

        return new AgwMessage(
            Guid.NewGuid().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = "HumanGate requires an active approval channel." }],
            additionalProperties);
    }

    private static AgwMessage CreateWorkflowErrorMessage(Exception? exception)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "workflow-error" },
        };

        return new AgwMessage(
            Guid.NewGuid().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = exception?.Message ?? "Workflow execution failed." }],
            additionalProperties);
    }

    private static HumanGateConfig ReadHumanGateConfig(AgentflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            return new HumanGateConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<HumanGateConfig>(node.ConfigJson, JsonOptions) ??
                new HumanGateConfig();
        }
        catch (JsonException)
        {
            return new HumanGateConfig();
        }
    }

    private sealed record HumanGateConfig
    {
        public string? HumanMode { get; init; }

        public string? HumanPrompt { get; init; }
    }

}
