using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Catalog;
using Agw.Agents.Definitions.Domain.Topology;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agentflows.Workflows;

/// <summary>Uses the execution compiler with descriptive nodes; rendering never creates external agents or sessions.</summary>
public sealed class AgentflowMermaidProvider : IAgentflowMermaidProvider
{
    private readonly IAgentflowDefinitionReader _definitions;
    private readonly IAgentCatalogFacade _catalog;
    private readonly AgentflowWorkflowCompiler _workflowCompiler;

    public AgentflowMermaidProvider(
        IAgentflowDefinitionReader definitions,
        IAgentCatalogFacade catalog,
        ILogger<AgentflowWorkflowCompiler> workflowCompilerLogger
    )
    {
        _definitions = definitions;
        _catalog = catalog;
        _workflowCompiler = new AgentflowWorkflowCompiler(workflowCompilerLogger);
    }

    public async Task<string?> GetMermaidAsync(
        Guid agentflowId,
        int agentflowRevision,
        CancellationToken cancellationToken = default
    )
    {
        var workflow = await CompileAsync(
            agentflowId,
            agentflowRevision,
            UserInfoUtil.RequiredUserId,
            new HashSet<Guid>(),
            cancellationToken
        );
        return workflow == null ? null : WorkflowVisualizer.ToMermaidString(workflow);
    }

    private async Task<Workflow?> CompileAsync(
        Guid id,
        int agentflowRevision,
        string owner,
        HashSet<Guid> ancestors,
        CancellationToken token
    )
    {
        if (!ancestors.Add(id))
            throw new AgwException(ErrorCodes.InvalidParam, $"Nested Agentflow cycle at '{id}'.");
        try
        {
            var flow = await _definitions.FindVisibleAsync(id, owner, token);
            if (flow == null)
                return null;
            var nodes = await _definitions.ListNodesAsync(id, agentflowRevision, token);
            var edges = await _definitions.ListEdgesAsync(id, agentflowRevision, token);
            var members = await _definitions.ListBlockMembersAsync(id, agentflowRevision, token);
            var agents = new Dictionary<string, AIAgent>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                if (node.Kind == AgentflowNodeKind.Agent && node.RelateId is { } agentId)
                {
                    if (!await _catalog.IsOwnedTargetAsync(AgentRuntimeType.Agent, agentId, owner, token))
                        return null;
                    agents[node.NodeId] = new DiagramAgent(node.NodeId, node.Name);
                }
                else if (node.Kind == AgentflowNodeKind.WorkflowAsAgent && node.RelateId is { } flowId)
                {
                    var target = await _definitions.FindVisibleAsync(flowId, owner, token);
                    if (target == null || target.PublishedRevision == 0)
                        return null;
                    var nested = await CompileAsync(flowId, target.PublishedRevision, owner, ancestors, token);
                    if (nested == null)
                        return null;
                    agents[node.NodeId] = nested.AsAIAgent();
                }
            }

            foreach (var member in members)
            {
                if (member.Kind == AgentflowNodeKind.Agent)
                {
                    if (!await _catalog.IsOwnedTargetAsync(AgentRuntimeType.Agent, member.RelateId, owner, token))
                        return null;
                    agents[AgentflowBlockMemberKey.Create(member.NodeId, member.MemberId)] = new DiagramAgent(
                        member.MemberId,
                        member.Name
                    );
                }
                else if (member.Kind == AgentflowNodeKind.WorkflowAsAgent)
                {
                    var target = await _definitions.FindVisibleAsync(member.RelateId, owner, token);
                    if (target == null || target.PublishedRevision == 0)
                        return null;
                    var nested = await CompileAsync(member.RelateId, target.PublishedRevision, owner, ancestors, token);
                    if (nested == null)
                        return null;
                    agents[AgentflowBlockMemberKey.Create(member.NodeId, member.MemberId)] = nested.AsAIAgent();
                }
            }

            var compilation = _workflowCompiler.Compile(
                flow,
                agentflowRevision,
                AgentflowTopology.OrderNodesByEdges(nodes, edges),
                edges,
                members,
                agents
            );
            return compilation?.Workflow;
        }
        finally
        {
            ancestors.Remove(id);
        }
    }

    private sealed class DiagramAgent : AIAgent
    {
        private readonly string? _name;

        public DiagramAgent(string id, string? name)
        {
            _name = name ?? id;
        }

        public override string? Name => _name;

        private static AgwException CannotExecute() => new(ErrorCodes.InvalidParam, "Diagram nodes cannot execute.");

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            throw CannotExecute();

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw CannotExecute();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => throw CannotExecute();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw CannotExecute();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw CannotExecute();
    }
}
