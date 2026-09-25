using System.Collections.Frozen;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Summaries;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agentflows.Runtime;

/// <summary>
/// 构造 AgentflowRuntime，并按当前用户加载图、编译 Workflow、准备节点会话范围与入口消息。
/// Builds AgentflowRuntimes, and loads the graph for the current user, compiles the Workflow and prepares node session scopes and entry messages.
/// </summary>
public sealed class AgentflowRuntimeFactory
{
    private readonly ILogger<AgentflowRuntimeFactory> _logger;
    private readonly IAgentflowDefinitionReader _definitions;
    private readonly IAgentRuntimeFactory _agentRuntimeFactory;
    private readonly IAgentTurnSummaryService _summaryService;
    private readonly IProviderSessionState _providerSessionState;
    private readonly IProjectDefaultResolver _projectDefaults;
    private readonly IProjectRuntimeFacade _projects;
    private readonly AgentSessionStateStore? _sessionStateStore;
    private readonly IConversationHistoryWriter? _conversationHistoryWriter;
    private readonly IConversationHandoffProvider? _conversationHandoffProvider;
    private readonly ExecutionPermissionService? _permissions;
    private readonly AgentflowWorkflowCompiler _workflowCompiler = new();

    public AgentflowRuntimeFactory(
        ILogger<AgentflowRuntimeFactory> logger,
        IAgentflowDefinitionReader definitions,
        IAgentRuntimeFactory agentRuntimeFactory,
        IAgentTurnSummaryService summaryService,
        IProviderSessionState providerSessionState,
        IProjectDefaultResolver projectDefaults,
        IProjectRuntimeFacade projects,
        AgentSessionStateStore? sessionStateStore = null,
        IConversationHistoryWriter? conversationHistoryWriter = null,
        IConversationHandoffProvider? conversationHandoffProvider = null,
        ExecutionPermissionService? permissions = null
    )
    {
        _logger = logger;
        _definitions = definitions;
        _agentRuntimeFactory = agentRuntimeFactory;
        _summaryService = summaryService;
        _providerSessionState = providerSessionState;
        _projectDefaults = projectDefaults;
        _projects = projects;
        _sessionStateStore = sessionStateStore;
        _conversationHistoryWriter = conversationHistoryWriter;
        _conversationHandoffProvider = conversationHandoffProvider;
        _permissions = permissions;
    }

    internal static AgentflowRuntime CreateRuntime(
        Guid agentflowId,
        AgentExecutionTask task,
        ExecutionSettings settings,
        bool deferHumanInteractions
    ) => new(agentflowId, task, settings, deferHumanInteractions);

    /// <summary>
    /// 为一个 Turn 读取当前用户可见的 Agentflow 与项目，并创建节点会话范围；Workflow 由调用方在历史作用域内编译。
    /// Reads the Agentflow and project visible to the current user for one turn and creates the node session scope; the caller compiles the Workflow inside its history scope.
    /// </summary>
    internal async Task<AgentflowTurnResources> PrepareTurnAsync(
        AgentflowRuntime runtime,
        ExecutionScope scope,
        CancellationToken cancellationToken
    )
    {
        var agentflow =
            await GetVisibleAgentflowAsync(runtime.AgentflowId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "The Agentflow was not found.");
        var projectId =
            await ResolveProjectIdAsync(runtime.Task.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new AgwException(ErrorCodes.ResourceNotFound, "The default project was not found.");
        if (await _projects.GetForCurrentUserAsync(projectId, cancellationToken).ConfigureAwait(false) == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, "The project was not found.");
        }

        var contextId = ContextIdUtil.ResolveContextId(runtime.Task.ContextId);
        var sessionScope = await CreateSessionScopeAsync(
                projectId,
                contextId,
                runtime.Task.TaskId,
                runtime.Task.ProjectConversationId,
                cancellationToken,
                new MafPermissionState(scope.Permissions)
            )
            .ConfigureAwait(false);
        return new AgentflowTurnResources(
            agentflow,
            sessionScope,
            new AgentflowExecutionTraceContext(projectId, contextId, runtime.Task.TaskId)
        );
    }

    internal static AgwUserInput CreateUserInput(string input) =>
        new() { Author = Constants.DefaultInputAuthor, Contents = [new AgwTextContent { Content = input }] };

    internal async Task<AgentflowAgentSessionScope> CreateSessionScopeAsync(
        Guid projectId,
        string contextId,
        Guid? taskId,
        Guid? conversationId,
        CancellationToken cancellationToken,
        MafPermissionState permissionState
    )
    {
        var resolvedConversationId =
            conversationId.HasValue && conversationId.Value != Guid.Empty
                ? conversationId.Value
                : await ResolveProjectConversationIdAsync(projectId, contextId, cancellationToken)
                    .ConfigureAwait(false);
        return new AgentflowAgentSessionScope(
            _providerSessionState,
            projectId,
            contextId.Trim(),
            taskId,
            _sessionStateStore,
            _conversationHistoryWriter,
            resolvedConversationId,
            permissionState
        );
    }

    internal async Task<List<ChatMessage>> CreateWorkflowInputMessagesAsync(
        Guid agentflowId,
        Guid conversationId,
        AgwUserInput input,
        CancellationToken cancellationToken
    )
    {
        var handoff =
            _conversationHandoffProvider == null
                ? ConversationHandoff.Empty
                : await _conversationHandoffProvider
                    .CreateAsync(conversationId, AgentRuntimeType.Agentflow, agentflowId, cancellationToken)
                    .ConfigureAwait(false);
        return AgwMessageUtil.CreateExecutionInputMessages(input, AgentRuntimeType.Agentflow, agentflowId, handoff);
    }

    public async Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default)
    {
        var agentflow = await GetVisibleAgentflowAsync(agentflowId, cancellationToken);
        if (agentflow == null)
        {
            return null;
        }

        var workflowLease = await CreateAiWorkflow(agentflow, cancellationToken);
        if (workflowLease == null)
        {
            return null;
        }

        await using (workflowLease)
        {
            var mermaidString = WorkflowVisualizer.ToMermaidString(workflowLease.Workflow);
            _logger.LogInformation("Constructed workflow: {Workflow}", mermaidString);
            return mermaidString;
        }
    }

    public async Task<AgentflowWorkflowLease?> CreateAiWorkflow(
        Guid agentflowId,
        CancellationToken cancellationToken = default
    )
    {
        var agentflow = await GetVisibleAgentflowAsync(agentflowId, cancellationToken);
        if (agentflow == null)
        {
            return null;
        }

        return await CreateAiWorkflow(agentflow, cancellationToken);
    }

    internal async Task<AgentflowWorkflowLease?> CreateAiWorkflow(
        Agentflow agentflow,
        CancellationToken cancellationToken,
        AgentflowAgentSessionScope? sessionScope = null,
        AgentflowExecutionTraceContext? executionTraceContext = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        bool deferHumanInteractions = false,
        IReadOnlySet<Guid>? ancestors = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_permissions != null)
            ExecutionPermissionService.Validate(
                await _permissions.GetAsync(AgentRuntimeType.Agentflow, agentflow.Id, cancellationToken),
                sessionScope?.PermissionState.Current
            );
        if (ancestors?.Contains(agentflow.Id) == true)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Nested Agentflow cycle at '{agentflow.Id}'.");
        }
        var path = new HashSet<Guid>(ancestors ?? Enumerable.Empty<Guid>()) { agentflow.Id };
        var agentflowNodes = await _definitions.ListNodesAsync(agentflow.Id, cancellationToken);
        var agentflowEdges = await _definitions.ListEdgesAsync(agentflow.Id, cancellationToken);
        if (agentflowNodes.Count == 0)
        {
            return null;
        }

        var orderedNodes = AgentflowTopology.OrderNodesByEdges(agentflowNodes, agentflowEdges);
        var nodeIdToAgent = new Dictionary<string, AIAgent>(StringComparer.Ordinal);
        var nodeEngineKinds = new Dictionary<string, EngineKind>(StringComparer.Ordinal);
        var resources = new AgentResourceLease();

        try
        {
            foreach (var node in orderedNodes)
            {
                AIAgent? aiAgent;
                if (node.Kind == AgentflowNodeKind.Agent && node.RelateId.HasValue)
                {
                    var nodeAgent = await _agentRuntimeFactory.CreateAgentflowNodeAgentAsync(
                        node.RelateId.Value,
                        sessionScope?.ProjectId,
                        sessionScope?.ConversationId ?? Guid.Empty,
                        environmentVariables,
                        deferHumanInteractions,
                        cancellationToken: cancellationToken,
                        permissionMode: sessionScope?.PermissionState.Current
                    );
                    aiAgent = nodeAgent?.Agent;
                    if (nodeAgent != null)
                    {
                        resources.Add(new AgentflowAgentLifetime(nodeAgent.Agent));
                        nodeEngineKinds[node.NodeId] = nodeAgent.EngineKind;
                    }
                }
                else if (node.Kind == AgentflowNodeKind.WorkflowAsAgent && node.RelateId.HasValue)
                {
                    var relatedAgentflow = await GetVisibleAgentflowAsync(node.RelateId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    if (relatedAgentflow == null)
                    {
                        await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
                        return null;
                    }

                    var flowNode = await CreateAiWorkflow(
                        relatedAgentflow,
                        cancellationToken,
                        sessionScope,
                        executionTraceContext,
                        environmentVariables,
                        deferHumanInteractions,
                        path
                    );
                    if (flowNode == null)
                    {
                        await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
                        return null;
                    }

                    resources.Add(flowNode);
                    aiAgent = flowNode.Workflow.AsAIAgent();
                }
                else
                {
                    continue;
                }

                if (aiAgent == null)
                {
                    await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
                    return null;
                }

                nodeIdToAgent[node.NodeId] = aiAgent;
            }

            if (nodeIdToAgent.Count == 0)
            {
                await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
                return null;
            }

            var summaryContext =
                sessionScope != null && agentflow.SummaryModelProviderId.HasValue
                    ? new AgentflowSummaryContext(
                        _summaryService,
                        agentflow.SummaryModelProviderId.Value,
                        sessionScope.ProjectId,
                        sessionScope.ContextId
                    )
                    : null;
            var workflow = _workflowCompiler.Compile(
                agentflow,
                orderedNodes,
                agentflowEdges,
                nodeIdToAgent,
                sessionScope,
                executionTraceContext,
                summaryContext,
                nodeEngineKinds
            );
            if (workflow == null)
            {
                await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
                return null;
            }

            var metadata = new AgentflowWorkflowMetadata(
                agentflowNodes
                    .Where(node => node.Kind == AgentflowNodeKind.HumanGate)
                    .ToFrozenDictionary(
                        node => node.NodeId,
                        node => new AgentflowHumanGateNode(node.NodeId, node.Name, node.ConfigJson),
                        StringComparer.Ordinal
                    ),
                agentflowNodes
                    .Where(node => node.Kind == AgentflowNodeKind.CheckpointMarker)
                    .ToFrozenDictionary(
                        node => AgentflowWorkflowCompiler.GetCheckpointRequestPortId(node.NodeId),
                        node => new CheckpointRequestNode(
                            node.NodeId,
                            AgentflowWorkflowCompiler.ResolveCheckpointName(node)
                        ),
                        StringComparer.Ordinal
                    )
            );
            return new AgentflowWorkflowLease(workflow, resources, metadata);
        }
        catch
        {
            await DisposeWorkflowResourcesWithoutThrowingAsync(resources).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<Agentflow?> GetVisibleAgentflowAsync(
        Guid agentflowId,
        CancellationToken cancellationToken = default
    )
    {
        return await _definitions
            .FindVisibleAsync(agentflowId, UserInfoUtil.RequiredUserId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask DisposeWorkflowResourcesWithoutThrowingAsync(IAsyncDisposable resources)
    {
        try
        {
            await resources.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
    }

    private async Task<Guid> ResolveProjectConversationIdAsync(
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken
    )
    {
        if (_sessionStateStore == null)
        {
            return Guid.Empty;
        }

        return await _sessionStateStore
                .ResolveProjectConversationIdAsync(projectId, contextId, cancellationToken)
                .ConfigureAwait(false)
            ?? Guid.Empty;
    }

    private async Task<Guid?> ResolveProjectIdAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (
            projectId != Guid.Empty
            && projectId != ProjectDefaults.DefaultBuiltInId
            && projectId != ProjectDefaults.A2AId
        )
        {
            return projectId;
        }

        return projectId == ProjectDefaults.A2AId
            ? await _projectDefaults.ResolveA2AProjectIdAsync(cancellationToken).ConfigureAwait(false)
            : await _projectDefaults.ResolveDefaultProjectIdAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 一个 Agentflow Turn 使用的定义、节点会话范围与节点执行追踪数据。
/// The definition, node session scope and node execution trace data of one Agentflow turn.
/// </summary>
internal sealed record AgentflowTurnResources(
    Agentflow Agentflow,
    AgentflowAgentSessionScope SessionScope,
    AgentflowExecutionTraceContext TraceContext
);
