using System.Collections.Frozen;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Agents.Execution.Agentflows.Observability;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agentflows;

/// <summary>
/// 按当前用户加载图并构建 Workflow、资源 Lease 与该次构建的节点元数据。
/// </summary>
public sealed class AgentflowWorkflowFactory
{
    private readonly ILogger<AgentflowRuntimeService> _logger;
    private readonly IAgentflowDefinitionReader _definitions;
    private readonly IAgentRuntimeService _agentRuntimeService;
    private readonly IAgentTurnSummaryService _summaryService;
    private readonly IRuntimeTurnContextAccessor _turnContextAccessor;
    private readonly AgentflowWorkflowCompiler _workflowCompiler = new();

    public AgentflowWorkflowFactory(
        ILogger<AgentflowRuntimeService> logger,
        IAgentflowDefinitionReader definitions,
        IAgentRuntimeService agentRuntimeService,
        IAgentTurnSummaryService summaryService,
        IRuntimeTurnContextAccessor turnContextAccessor
    )
    {
        _logger = logger;
        _definitions = definitions;
        _agentRuntimeService = agentRuntimeService;
        _summaryService = summaryService;
        _turnContextAccessor = turnContextAccessor;
    }

    public async Task<string?> GetMermaidAsync(Guid agentflowId, CancellationToken cancellationToken = default)
    {
        var agentflow = await GetVisibleAgentflowAsync(agentflowId);
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
        var agentflow = await GetVisibleAgentflowAsync(agentflowId);
        if (agentflow == null)
        {
            return null;
        }

        return await CreateAiWorkflow(agentflow, cancellationToken);
    }

    private async Task<AgentflowWorkflowLease?> CreateAiWorkflow(
        Guid agentflowId,
        CancellationToken cancellationToken,
        AgentflowAgentSessionScope? sessionScope,
        AgentflowExecutionTraceContext? executionTraceContext = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        bool deferHumanInteractions = false
    )
    {
        var agentflow = await GetVisibleAgentflowAsync(agentflowId);
        if (agentflow == null)
        {
            return null;
        }

        return await CreateAiWorkflow(
            agentflow,
            cancellationToken,
            sessionScope,
            executionTraceContext,
            environmentVariables,
            deferHumanInteractions
        );
    }

    internal async Task<AgentflowWorkflowLease?> CreateAiWorkflow(
        Agentflow agentflow,
        CancellationToken cancellationToken,
        AgentflowAgentSessionScope? sessionScope = null,
        AgentflowExecutionTraceContext? executionTraceContext = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        bool deferHumanInteractions = false
    )
    {
        var agentflowNodes = await _definitions.ListNodesAsync(agentflow.Id, cancellationToken);
        var agentflowEdges = await _definitions.ListEdgesAsync(agentflow.Id, cancellationToken);
        if (agentflowNodes.Count == 0)
        {
            return null;
        }

        var orderedNodes = AgentflowTopology.OrderNodesByEdges(agentflowNodes, agentflowEdges);
        var nodeIdToAgent = new Dictionary<string, AIAgent>(StringComparer.Ordinal);
        var resources = new AgentResourceLease();

        try
        {
            foreach (var node in orderedNodes)
            {
                AIAgent? aiAgent;
                if (node.Kind == AgentflowNodeKind.Agent && node.RelateId.HasValue)
                {
                    aiAgent = await _agentRuntimeService.CreateAgentflowNodeAgentAsync(
                        node.RelateId.Value,
                        sessionScope?.ProjectId,
                        sessionScope?.ConversationId ?? Guid.Empty,
                        environmentVariables,
                        deferHumanInteractions,
                        cancellationToken: cancellationToken
                    );
                    if (aiAgent != null)
                    {
                        resources.Add(new AgentflowAgentLifetime(aiAgent));
                    }
                }
                else if (node.Kind == AgentflowNodeKind.WorkflowAsAgent && node.RelateId.HasValue)
                {
                    var relatedAgentflow = await GetVisibleAgentflowAsync(node.RelateId.Value).ConfigureAwait(false);
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
                        deferHumanInteractions
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
                summaryContext
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

    private static async ValueTask DisposeWorkflowResourcesWithoutThrowingAsync(IAsyncDisposable resources)
    {
        try
        {
            await resources.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
    }

    internal async Task<Agentflow?> GetVisibleAgentflowAsync(Guid agentflowId)
    {
        var ownerUserId = ResolveExecutionUserId();
        return await _definitions.FindVisibleAsync(agentflowId, ownerUserId).ConfigureAwait(false);
    }

    internal string ResolveExecutionUserId()
    {
        return TryResolveExecutionUserId() ?? throw new AgwException(ErrorCodes.AuthenticationRequired);
    }

    private string? TryResolveExecutionUserId()
    {
        if (UserInfoUtil.IsContextActive)
        {
            return UserInfoUtil.RequiredUserId;
        }

        var userId = _turnContextAccessor.Current?.UserId;
        return string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();
    }
}
